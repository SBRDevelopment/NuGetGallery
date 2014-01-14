﻿using System;
using System.Reactive.Linq;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Practices.EnterpriseLibrary.SemanticLogging;
using Microsoft.Practices.EnterpriseLibrary.SemanticLogging.Formatters;
using Microsoft.Practices.EnterpriseLibrary.SemanticLogging.Sinks;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System.Reactive.Subjects;
using System.Reactive.Disposables;
using Microsoft.WindowsAzure.Storage.Blob;
using NuGet.Services.Work.Monitoring;
using System.Net;
using NuGet.Services.Storage;
using NuGet.Services.Work.Configuration;
using Autofac.Core;
using Autofac;
using NuGet.Services.ServiceModel;
using NuGet.Services.Work.Api.Models;
using NuGet.Services.Configuration;
using NuGet.Services.Work.Models;
using System.Threading;

namespace NuGet.Services.Work
{
    public class WorkService : NuGetService
    {
        internal const string InvocationLogsContainerBaseName = "work-invocations";
        public static readonly string MyServiceName = "Work";

        private JobRunner _runner;
        private InvocationQueue _queue;

        public IEnumerable<JobDescription> Jobs { get; private set; }

        public WorkService(ServiceHost host)
            : this(host, null)
        {
        }

        public WorkService(ServiceHost host, InvocationQueue queue)
            : base(MyServiceName, host)
        {
            _queue = queue;
        }

        protected override async Task<bool> OnStart()
        {
            try
            {
                DiscoverJobs();

                return await base.OnStart();
            }
            catch (Exception ex)
            {
                // Exceptions that escape to this level are fatal
                WorkServiceEventSource.Log.StartupError(ex);
                return false;
            }
        }

        protected override Task OnRun()
        {
            var queueConfig = Configuration.GetSection<QueueConfiguration>();
            _runner = Container.Resolve<JobRunner>();
            _runner.Heartbeat += (_, __) => Heartbeat();

            return _runner.Run(Host.ShutdownToken);
        }

        private void DiscoverJobs()
        {
            Jobs = Container.Resolve<IEnumerable<JobDescription>>();

            foreach (var job in Jobs)
            {
                // Record the discovery in the trace
                WorkServiceEventSource.Log.JobDiscovered(job);
            }
        }

        public override void RegisterComponents(ContainerBuilder builder)
        {
            base.RegisterComponents(builder);

            var jobdefs = GetAllAvailableJobs();
            builder.RegisterInstance(jobdefs).As<IEnumerable<JobDescription>>();

            builder.RegisterModule(new JobComponentsModule(_queue));
        }

        public static IEnumerable<JobDescription> GetAllAvailableJobs()
        {
            var jobdefs = typeof(WorkWorkerRole)
                   .Assembly
                   .GetExportedTypes()
                   .Where(t => !t.IsAbstract && typeof(JobHandlerBase).IsAssignableFrom(t))
                   .Select(t => JobDescription.Create(t))
                   .Where(d => d != null);
            return jobdefs;
        }

        public override Task<object> GetCurrentStatus()
        {
            if (_runner != null)
            {
                return _runner.GetCurrentStatus();
            }
            return Task.FromResult<object>(null);
        }

        public override Task<object> Describe()
        {
            return Task.FromResult<object>(new WorkServiceModel(Jobs));
        }

        public IObservable<EventEntry> RunJob(string job, string payload)
        {
            var invocation = 
                new InvocationState(
                    new InvocationState.InvocationRow() {
                        Payload = payload,
                        Status = (int)InvocationStatus.Executing,
                        Result = (int)ExecutionResult.Incomplete,
                        Source = Constants.Source_LocalJob,
                        Id = Guid.NewGuid(),
                        Job = job,
                        UpdatedBy = Environment.MachineName,
                        UpdatedAt = DateTime.UtcNow,
                        QueuedAt = DateTime.UtcNow,
                        NextVisibleAt = DateTime.UtcNow + TimeSpan.FromMinutes(5)
                    });
            var buffer = new ReplaySubject<EventEntry>();
            var capture = new InvocationLogCapture(invocation);
            capture.Subscribe(buffer.OnNext, buffer.OnError);
            _runner.Dispatch(invocation, capture, CancellationToken.None).ContinueWith(t => {
                if (t.IsFaulted)
                {
                    buffer.OnError(t.Exception);
                }
                else
                {
                    buffer.OnCompleted();
                }
                return t;
            });
            return buffer;
        }
    }
}