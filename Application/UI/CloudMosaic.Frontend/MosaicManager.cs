﻿using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using System.IO;

using Amazon.Batch;
using Amazon.Batch.Model;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;

using Amazon.ECS;
using Amazon.ECS.Model;
using KeyValuePair = Amazon.ECS.Model.KeyValuePair;

using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

using CloudMosaic.Frontend.Models;
using CloudMosaic.Common;

using Newtonsoft.Json;

namespace CloudMosaic.Frontend
{
    public class MosaicManager
    {
        AppOptions _appOptions;

        IAmazonBatch _batchClient;
        IAmazonDynamoDB _ddbClient;
        IAmazonECS _ecsClient;
        IAmazonS3 _s3Client;
        ITransferUtility _s3TransferUtility;
        IAmazonStepFunctions _stepClient;

        DynamoDBContext _ddbContext;

        public MosaicManager(IOptions<AppOptions> appOptions, IAmazonBatch batchClient, IAmazonDynamoDB ddbClient, IAmazonECS ecsClient, IAmazonS3 s3Client, IAmazonStepFunctions stepClient)
        {
            this._appOptions = appOptions.Value;

            this._batchClient = batchClient;
            this._ddbClient = ddbClient;
            this._ecsClient = ecsClient;
            this._s3Client = s3Client;
            this._s3TransferUtility = new TransferUtility(s3Client);
            this._stepClient = stepClient;

            this._ddbContext = new DynamoDBContext(this._ddbClient);
        }

        public async Task<Mosaic> CreateMosaic(string userId, string galleryId, string name, Stream stream)
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var mosaicId = name + "-" + Guid.NewGuid().ToString();
                using (var fileStream = File.OpenWrite(tempFile))
                {
                    Utilities.CopyStream(stream, fileStream);
                }

                var putRequest = new PutObjectRequest
                {
                    BucketName = this._appOptions.MosaicStorageBucket,
                    Key = S3KeyManager.DetermineS3Key(userId, mosaicId, S3KeyManager.ImageType.Original),
                    FilePath = tempFile
                };
                await this._s3Client.PutObjectAsync(putRequest).ConfigureAwait(false);

                var mosaic = new Mosaic
                {
                    UserId = userId,
                    MosaicId = mosaicId,
                    CreateDate = DateTime.UtcNow,
                    Name = name,
                    Status = Mosaic.Statuses.Creating
                };

                var input = new ExecutionInput
                {
                    TableGalleryItems = this._appOptions.TableGalleryItems,
                    TableMosaic = this._appOptions.TableMosaic,
                    Bucket = this._appOptions.MosaicStorageBucket,
                    SourceKey = putRequest.Key,
                    GalleryId = galleryId,
                    MosaicId = mosaicId,
                    UserId = userId
                };

                var stepResponse = await this._stepClient.StartExecutionAsync(new StartExecutionRequest
                {
                    StateMachineArn = this._appOptions.StateMachineArn,
                    Name = $"{Utilities.MakeSafeName(putRequest.Key, 80)}",
                    Input = JsonConvert.SerializeObject(input)
                }).ConfigureAwait(false);

                mosaic.ExecutionArn = stepResponse.ExecutionArn;
                await this._ddbContext.SaveAsync(mosaic).ConfigureAwait(false);

                return mosaic;
            }
            finally
            {
                if(File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        public async Task StartGalleryImport(string userId, string galleryId, Stream stream)
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                using (var fileStream = File.OpenWrite(tempFile))
                {
                    Utilities.CopyStream(stream, fileStream);
                }

                var transferRequest = new TransferUtilityUploadRequest
                {
                    BucketName = this._appOptions.MosaicStorageBucket,
                    Key = S3KeyManager.DetermineS3Key(userId, galleryId, S3KeyManager.ImageType.TileGallerySource),
                    FilePath = tempFile
                };
                await this._s3TransferUtility.UploadAsync(transferRequest).ConfigureAwait(false);

                // Default verb is GET, and 60min validity should be more than sufficient!
                var importUrl = this._s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = transferRequest.BucketName,
                    Key = transferRequest.Key,
                    Expires = DateTime.Now.AddHours(1)
                });

                var submitRequest = new SubmitJobRequest
                {
                    JobQueue = this._appOptions.JobQueueArn,
                    JobDefinition = this._appOptions.JobDefinitionArn,
                    JobName = $"{Utilities.MakeSafeName(galleryId, 128)}",
                    ContainerOverrides = new ContainerOverrides
                    {
                        Environment = new List<Amazon.Batch.Model.KeyValuePair>
                        {
                            new Amazon.Batch.Model.KeyValuePair{Name = Constants.ZIP_EXPANDER_BUCKET, Value = this._appOptions.MosaicStorageBucket},
                            new Amazon.Batch.Model.KeyValuePair{Name = Constants.ZIP_EXPANDER_DDB_TABLE, Value = this._appOptions.TableGallery},
                            new Amazon.Batch.Model.KeyValuePair{Name = Constants.ZIP_EXPANDER_USER_ID, Value = userId},
                            new Amazon.Batch.Model.KeyValuePair{Name = Constants.ZIP_EXPANDER_GALLERY_ID, Value = galleryId},
                            new Amazon.Batch.Model.KeyValuePair{Name = Constants.ZIP_EXPANDER_IMPORT_URL, Value = importUrl}
                        }
                    }
                };

                await this._batchClient.SubmitJobAsync(submitRequest).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        public async Task AlternativeStartGalleryImport(string userId, string galleryId, string importUrl)
        {
            var runRequest = new RunTaskRequest
            {
                Cluster = this._appOptions.ECSCluster,
                TaskDefinition = this._appOptions.ECSTaskDefinition,
                Overrides = new TaskOverride
                {
                    ContainerOverrides = new List<ContainerOverride>
                    {
                        new ContainerOverride
                        {
                            Name = this._appOptions.ECSContainerDefinition,
                            Environment = new List<KeyValuePair>
                            {
                                new KeyValuePair{Name = Constants.ZIP_EXPANDER_BUCKET, Value = this._appOptions.MosaicStorageBucket},
                                new KeyValuePair{Name = Constants.ZIP_EXPANDER_DDB_TABLE, Value = this._appOptions.TableGallery},
                                new KeyValuePair{Name = Constants.ZIP_EXPANDER_USER_ID, Value = userId},
                                new KeyValuePair{Name = Constants.ZIP_EXPANDER_GALLERY_ID, Value = galleryId},
                                new KeyValuePair{Name = Constants.ZIP_EXPANDER_IMPORT_URL, Value = importUrl}
                            }
                        }
                    }
                },

                LaunchType = LaunchType.FARGATE,
                NetworkConfiguration = new NetworkConfiguration
                {
                    AwsvpcConfiguration = new AwsVpcConfiguration
                    {
                        SecurityGroups = this._appOptions.FargateSecurityGroup.Split(',').ToList(),
                        Subnets = this._appOptions.FargateSubnet.Split(',').ToList(),
                        AssignPublicIp = AssignPublicIp.ENABLED
                    }
                }                
            };

            await this._ecsClient.RunTaskAsync(runRequest).ConfigureAwait(false);
        }
    }
}
