﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BrightstarDB.Client;
using BrightstarDB.Model;
using BrightstarDB.Query;
using BrightstarDB.Rdf;
using BrightstarDB.Storage;
using BrightstarDB.Storage.Persistence;
#if !SILVERLIGHT
using System.ServiceModel;
using BrightstarDB.Storage.BTreeStore;
#endif

namespace BrightstarDB.Server
{

    ///<summary>
    /// Called by the store worker after the thread has been successfully terminated.
    ///</summary>
    internal delegate void ShutdownContinuation(); 

    /// <summary>
    /// This can be considered a logical store. A logical store is comprised of 2 physical stores. A read store and a write store.
    /// Read requests coming in to the store worker are dispatched to the readStore. The readStore pulls data in from the disk as required, until 
    /// it reaches some predefined levels at which point the cache is flushed.
    /// Update transactions coming to the logical store are put in a queue. The logical store has a worker thread that processes each transaction
    /// one at time. All updates are made against the writeStore. When a transaction completes the read store instance is invalidated so that the 
    /// next read request to arrive forces the data to be re-read from disk.
    /// </summary>
    internal class StoreWorker
    {
        // write store
        private IStore _writeStore;

        // read store
        private IStore _readStore;

        // store name
        private readonly string _storeName;

        // store location
        private readonly string _storeLocation;

        // job queue
        private readonly ConcurrentQueue<Job> _jobs;

        private readonly ITransactionLog _transactionLog;

        // todo: might need to make this persistent or clear it out now and again.
        private readonly ConcurrentDictionary<string, JobExecutionStatus> _jobExecutionStatus;

        private bool _shutdownRequested;
        private bool _completeRemainingJobs;
        private Thread _jobProcessingThread;

        /// <summary>
        /// Event fired after a successful job execution but before the job status is updated to completed
        /// </summary>
        internal event JobCompletedDelegate JobCompleted;

        /// <summary>
        /// Called after we shutdown all jobs and close all file handles.
        /// </summary>
        private ShutdownContinuation _shutdownContinuation; 

        /// <summary>
        /// Creates a new server core 
        /// </summary>
        /// <param name="baseLocation">Path to the stores directory</param>
        /// <param name="storeName">Name of store</param>
        public StoreWorker(string baseLocation, string storeName)
        {
            _storeName = storeName;
            _storeLocation = Path.Combine(baseLocation, storeName);
            Logging.LogInfo("StoreWorker created with location {0}", _storeLocation);
            _jobs = new ConcurrentQueue<Job>();
            // _jobStatus = new ConcurrentDictionary<string, JobStatus>();
            _jobExecutionStatus = new ConcurrentDictionary<string, JobExecutionStatus>();
            _storeManager = StoreManagerFactory.GetStoreManager();
            _transactionLog = _storeManager.GetTransactionLog(_storeLocation);
        }

        /// <summary>
        /// Creates a new server core that uses a specific IStoreManager implementation
        /// rather than getting one from the StoreManagerFactory
        /// </summary>
        /// <param name="baseLocation"></param>
        /// <param name="storeName"></param>
        /// <param name="storeManager"></param>
        public StoreWorker(string baseLocation, string storeName, IStoreManager storeManager)
        {
            _storeName = storeName;
            _storeLocation = Path.Combine(baseLocation, storeName);
            Logging.LogInfo("StoreWorker created with location {0}", _storeLocation);
            _jobs = new ConcurrentQueue<Job>();
            _jobExecutionStatus = new ConcurrentDictionary<string, JobExecutionStatus>();
            _storeManager = storeManager;
            _transactionLog = _storeManager.GetTransactionLog(_storeLocation);
        }

        /// <summary>
        /// Starts the thread that processes the jobs queue.
        /// </summary>
        public void Start()
        {
            _jobProcessingThread = new Thread(ProcessJobs);
            _jobProcessingThread.Start();
        }

        public string StoreLocation
        {
            get { return _storeLocation; }
        }

        public ITransactionLog TransactionLog
        {
            get { return _transactionLog; }
        }

        private void ProcessJobs()
        {
            Logging.LogInfo("Process Jobs Started");

            while (!_shutdownRequested)
            {
                try
                {
                    if (_shutdownRequested && !_completeRemainingJobs) break;
                    Job job = null;
                    _jobs.TryDequeue(out job);
                    if (job != null)
                    {
                        Logging.LogInfo("Job found {0}", job.JobId);

                        // process job
                        JobExecutionStatus jobExecutionStatus;
                        if (_jobExecutionStatus.TryGetValue(job.JobId.ToString(), out jobExecutionStatus))
                        {

                            try
                            {
                                jobExecutionStatus.Information = "Job Started";
                                jobExecutionStatus.Started = DateTime.UtcNow;
                                jobExecutionStatus.JobStatus = JobStatus.Started;

                                var st = DateTime.UtcNow;
                                job.Run();
                                var et = DateTime.UtcNow;
#if SILVERLIGHT
                                Logging.LogInfo("Job completed in {0}", et.Subtract(st).TotalMilliseconds);
#else
                                Logging.LogInfo("Job completed in {0} : Current memory usage : {1}",et.Subtract(st).TotalMilliseconds, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 );
#endif
                                jobExecutionStatus.Information = "Job Completed";
                                jobExecutionStatus.Ended = DateTime.UtcNow;
                                jobExecutionStatus.JobStatus = JobStatus.CompletedOk;
                            }
                            catch (Exception ex)
                            {
                                Logging.LogError(BrightstarEventId.JobProcessingError,
                                                 "Error Processing Transaction {0}",
                                                 ex);
                                jobExecutionStatus.Information = job.ErrorMessage ?? "Job Error";
                                jobExecutionStatus.Ended = DateTime.UtcNow;
                                jobExecutionStatus.ExceptionDetail = new ExceptionDetail(ex);
                                jobExecutionStatus.JobStatus = JobStatus.TransactionError;
                            }
                            finally
                            {
                                if (JobCompleted!= null)
                                {
                                    JobCompleted(this, new JobCompletedEventArgs(_storeName, job));
                                }
                            }
                        }
                    }
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    Logging.LogError(BrightstarEventId.JobProcessingError,
                                     "Unexpected exception caught in ProcessJobs loop: {0}", ex);
                }
            }

            ReleaseResources();
            if (_shutdownContinuation != null)
            {
                try
                {
                    _shutdownContinuation();
                }
                catch (Exception ex)
                {
                    Logging.LogError(BrightstarEventId.JobProcessingError,
                                     "Unexpected exception caught in processing Shutdown continuation: {0}", ex);
                }
            }
        }

        private void RaiseTransactionCommitting(Job job)
        {
            if (JobCompleted != null)
            {
                JobCompleted(this, new JobCompletedEventArgs(_storeName, job));
            }
        }

        public JobExecutionStatus GetJobStatus(string jobId)
        {
            JobExecutionStatus status;
            if (_jobExecutionStatus.TryGetValue(jobId, out status))
            {
                return status;
            }

            return null;
        }

        /// <summary>
        /// This is used to ensure there is no race condition when returning
        /// the readstore when commits are occurring.
        /// </summary>
        private readonly object _readStoreLock = new object();

        private readonly IStoreManager _storeManager;

        internal void InvalidateReadStore()
        {
            lock(_readStoreLock)
            {
                if (_readStore != null)
                {
                    _readStore.Close();
                }
                _readStore = null;
            }
        }

        internal IStore ReadStore
        {
            get
            {
                lock (_readStoreLock)
                {
                    if (_readStore == null)
                    {
                        _readStore = _storeManager.OpenStore(_storeLocation, true);
                    }
                    return _readStore;
                }
            }
        }

        internal IStore WriteStore
        {
            get
            {
                if (_writeStore == null)
                {
                    _writeStore = _storeManager.OpenStore(_storeLocation, false);
                }
                return _writeStore;
            }            
        }

        public void Query(ulong commitPointId, string queryExpression, SparqlResultsFormat resultsFormat, Stream resultsStream)
        {
            // Not supported by read/write store so no handling for ReadWriteStoreModifiedException required
            using (var readStore = _storeManager.OpenStore(_storeLocation, commitPointId))
            {
                BrightstarSparqlResultsType resultsType;
                readStore.ExecuteSparqlQuery(queryExpression, resultsFormat, resultsStream, out resultsType);
            }
        }

        public void Query(string queryExpression, SparqlResultsFormat resultsFormat, Stream resultsStream)
        {
            Logging.LogDebug("Query {0}", queryExpression);
            try
            {
                BrightstarSparqlResultsType resultsType;
                ReadStore.ExecuteSparqlQuery(queryExpression, resultsFormat, resultsStream, out resultsType);
            }
            catch (ReadWriteStoreModifiedException)
            {
                Logging.LogDebug("Read/Write store was concurrently modified. Attempting a retry");
                InvalidateReadStore();
                Query(queryExpression, resultsFormat, resultsStream);
            }
        }

        public string Query(ulong commitPointId, string queryExpression, SparqlResultsFormat resultsFormat)
        {
            // Not supported by read/write store so no handling for ReadWriteStoreModifiedException required
            Logging.LogDebug("CommitPointId={0}, Query={1}", commitPointId, queryExpression);
            using (var readStore = _storeManager.OpenStore(_storeLocation, commitPointId))
            {
                return readStore.ExecuteSparqlQuery(queryExpression, resultsFormat);
            }
        }

        public string Query(string queryExpression, SparqlResultsFormat resultsFormat)
        {
            Logging.LogDebug("Query {0}", queryExpression);
            try
            {
                return ReadStore.ExecuteSparqlQuery(queryExpression, resultsFormat);
            }
            catch (ReadWriteStoreModifiedException)
            {
                Logging.LogDebug("Read/Write store was concurrently modified. Attempting a retry");
                InvalidateReadStore();
                return Query(queryExpression, resultsFormat);
            }
        }

        public void QueueJob(Job job)
        {
            Logging.LogDebug("Queueing Job Id {0}", job.JobId);
            _jobExecutionStatus.TryAdd(job.JobId.ToString(), new JobExecutionStatus { JobId = job.JobId, JobStatus = JobStatus.Pending });
            _jobs.Enqueue(job);
        }

        /// <summary>
        /// Queue a txn job.
        /// </summary>
        /// <param name="preconditions">The triples that must be present for txn to succeed</param>
        /// <param name="deletePatterns"></param>
        /// <param name="insertData"></param>
        /// <param name="format"></param>
        /// <returns></returns>
        public Guid ProcessTransaction(string preconditions, string deletePatterns, string insertData, string format)
        {
            Logging.LogInfo("ProcessTransaction");           
            var jobId = Guid.NewGuid();
            var job = new UpdateTransaction(jobId, this, preconditions, deletePatterns, insertData);
            Logging.LogDebug("Queueing Job Id {0}", jobId);
            _jobs.Enqueue(job);
            _jobExecutionStatus.TryAdd(jobId.ToString(), new JobExecutionStatus { JobId = jobId, JobStatus = JobStatus.Pending });
            return jobId;
        }

        public Guid Insert(String data, string format)
        {
            var jobId = Guid.NewGuid();
            _jobs.Enqueue(new UpdateTransaction(jobId, this,"", "", data));
            _jobExecutionStatus.TryAdd(jobId.ToString(), new JobExecutionStatus { JobId = jobId, JobStatus = JobStatus.Pending });
            return jobId;
        }

        public void ExportData(Stream stream)
        {
            ExportData(stream, Constants.DefaultGraphUri.ToString());
        }

        public void ExportData(Stream stream, string graphUri)
        {
            try
            {
                var triples = ReadStore.Match(null, null, null, graph: graphUri);
                using (var sw = new StreamWriter(stream))
                {
                    var nw = new BrightstarTripleSinkAdapter(new NTriplesWriter(sw));
                    foreach (var triple in triples)
                    {
                        nw.Triple(triple);
                    }
                    sw.Flush();
                }
            }
            catch (ReadWriteStoreModifiedException)
            {
                Logging.LogError(BrightstarEventId.ExportDataError, "Store was modified while export was running.");
            }
            catch (Exception ex)
            {
                Logging.LogError(BrightstarEventId.ExportDataError, "Error Exporting Data {0} {1}", ex.Message,
                                 ex.StackTrace);
            }
        }

        public Guid Import(string contentFileName, string graphUri)
        {
            var jobId = Guid.NewGuid();
            bool queuedJob = false;
            while (!queuedJob)
            {
                if (_jobExecutionStatus.TryAdd(jobId.ToString(),
                                               new JobExecutionStatus {JobId = jobId, JobStatus = JobStatus.Pending}))
                {
                    _jobs.Enqueue(new ImportJob(jobId, this, contentFileName, graphUri));
                    queuedJob = true;
                    Logging.LogInfo("Queued import job {0}", jobId);
                }
            }
            return jobId;
        }

        public Guid Export(string fileName, string graphUri)
        {
            var jobId = Guid.NewGuid();
            var exportJob = new ExportJob(jobId, this, fileName, graphUri);
            _jobExecutionStatus.TryAdd(jobId.ToString(),
                                       new JobExecutionStatus {JobId = jobId, JobStatus = JobStatus.Started});
            exportJob.Run((id, ex) =>
                              {
                                  JobExecutionStatus jobExecutionStatus;
                                  _jobExecutionStatus.TryGetValue(id.ToString(), out jobExecutionStatus);
                                  jobExecutionStatus.Information = "Export failed";
                                  jobExecutionStatus.ExceptionDetail = new ExceptionDetail(ex);
                                  jobExecutionStatus.JobStatus = JobStatus.TransactionError;
                                  jobExecutionStatus.Ended = DateTime.UtcNow;
                              },
                          id =>
                              {
                                  JobExecutionStatus jobExecutionStatus;
                                  _jobExecutionStatus.TryGetValue(id.ToString(), out jobExecutionStatus);
                                  jobExecutionStatus.Information = "Export completed";
                                  jobExecutionStatus.JobStatus = JobStatus.CompletedOk;
                                  jobExecutionStatus.Ended = DateTime.UtcNow;
                              });
            return jobId;
        }


        public IEnumerable<Triple> GetResourceStatements(string resourceUri)
        {
            Logging.LogDebug("GetResourceStatements {0}", resourceUri);
            try
            {
                return ReadStore.GetResourceStatements(resourceUri);
            }
            catch (ReadWriteStoreModifiedException)
            {
                Logging.LogDebug("Read/Write store was concurrently modified. Attempting retry.");
                InvalidateReadStore();
                return GetResourceStatements(resourceUri);
            }
        }

        private void ReleaseResources()
        {
            // close file handles
            if (_readStore != null)
            {
                _readStore.Close();
            }

            if (_writeStore != null)
            {
                _writeStore.Close();
            }            
        }

        public void Shutdown(bool completeJobs, ShutdownContinuation c = null)
        {
            _shutdownContinuation = c;
            _shutdownRequested = true;
            _completeRemainingJobs = completeJobs;
        }

        public void Consolidate(Guid jobId)
        {
            ReleaseResources();
            _writeStore = null;
            _readStore = null;
            WriteStore.Consolidate(jobId);
            ReleaseResources();
            _writeStore = null;
            _readStore = null;    
        }

    }
}
