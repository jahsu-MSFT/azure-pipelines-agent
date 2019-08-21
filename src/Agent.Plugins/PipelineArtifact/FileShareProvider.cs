﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Agent.Sdk;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Content.Common;


namespace Agent.Plugins.PipelineArtifact
{
    internal class FileShareProvider: IArtifactProvider
    {
        private readonly AgentTaskPluginExecutionContext context;
        private readonly CallbackAppTraceSource tracer;
        private const int defaultParallelCount = 1;

        public FileShareProvider(AgentTaskPluginExecutionContext context, CallbackAppTraceSource tracer)
        {
            this.context = context;
            this.tracer = tracer;
        }

        public async Task DownloadSingleArtifactAsync(PipelineArtifactDownloadParameters downloadParameters, BuildArtifact buildArtifact, CancellationToken cancellationToken)
        {
            var downloadRootPath = Path.Combine(buildArtifact.Resource.Data, buildArtifact.Name);
            await this.CopyFileShareAsync(downloadRootPath, Path.Combine(downloadParameters.TargetDirectory, buildArtifact.Name), downloadParameters.MinimatchFilters, cancellationToken);
        }

        public async Task DownloadMultipleArtifactsAsync(PipelineArtifactDownloadParameters downloadParameters, IEnumerable<BuildArtifact> buildArtifacts, CancellationToken cancellationToken)
        {
            foreach (var buildArtifact in buildArtifacts)
            {
                var dirPath = Path.Combine(downloadParameters.TargetDirectory, buildArtifact.Name);
                var downloadRootPath = buildArtifact.Resource.Data;
                await this.CopyFileShareAsync(downloadRootPath, dirPath, downloadParameters.MinimatchFilters, cancellationToken);
            }
        }

        public async Task PublishArtifactAsync(string sourcePath, string destPath, int parallelCount, CancellationToken cancellationToken)
        {
            await this.DirectoryCopyWithMiniMatch(sourcePath, destPath, cancellationToken, parallelCount);
        }

        private async Task CopyFileShareAsync(string downloadRootPath, string destPath, IEnumerable<string> minimatchPatterns, CancellationToken cancellationToken)
        {
            minimatchPatterns = minimatchPatterns.Select(pattern => Path.Combine(downloadRootPath, pattern));
            IEnumerable<Func<string, bool>> minimatcherFuncs = MinimatchHelper.GetMinimatchFuncs(minimatchPatterns, this.tracer);
            await DirectoryCopyWithMiniMatch(downloadRootPath, destPath, cancellationToken, defaultParallelCount, minimatcherFuncs);
        }

        private async Task DirectoryCopyWithMiniMatch(string sourcePath, string destPath, CancellationToken cancellationToken, int parallelCount = defaultParallelCount, IEnumerable<Func<string, bool>> minimatchFuncs = null)
        {
            // If the source path is a file, the system should copy the file to the dest directory directly. 
            if(File.Exists(sourcePath)) 
            {
                destPath = destPath + Path.DirectorySeparatorChar + Path.GetFileName(sourcePath);
                this.context.Output(StringUtil.Loc("CopyFileToDestination", sourcePath, destPath));
                File.Copy(sourcePath, destPath, true);
                await Task.CompletedTask;
                return;
            }

            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourcePath);

            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(destPath))
            {
                Directory.CreateDirectory(destPath);
            }
   
            // Get the files in the directory and copy them to the new location.
            var files = dir.GetFiles("*", SearchOption.AllDirectories);

            var parallelism = new ExecutionDataflowBlockOptions()
            {
                MaxDegreeOfParallelism = parallelCount,
                BoundedCapacity = 2 * parallelCount,
                CancellationToken = cancellationToken
            };

            var actionBlock = NonSwallowingActionBlock.Create<FileInfo>(
                action: file =>
                {
                    if (minimatchFuncs == null || minimatchFuncs.Any(match => match(file.FullName))) 
                    {
                        string tempPath = Path.Combine(destPath, Path.GetRelativePath(sourcePath, file.FullName));
                        this.context.Output(StringUtil.Loc("CopyFileToDestination", file, tempPath));
                        FileInfo tempFile = new System.IO.FileInfo(tempPath);
                        tempFile.Directory.Create(); // If the directory already exists, this method does nothing.
                        file.CopyTo(tempPath, true);
                    }
                },
                dataflowBlockOptions: parallelism);

            await actionBlock.SendAllAndCompleteAsync(files, actionBlock, cancellationToken);
        }
    }
}
