﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Caching;

namespace CircuiteBreaker
{
    public class CircuitBreaker
    {
        private static ConcurrentDictionary<string, object> CircuitBreakerLocks = new ConcurrentDictionary<string, object>();
        private static ConcurrentDictionary<string, int> CircuitBreakerExceptionCounts = new ConcurrentDictionary<string, int>();

        public string CircuitId { get; set; }

        public object CircuitBreakLock
        {
            get
            {
                return CircuitBreakerLocks.GetOrAdd(CircuitId, new object());
            }
        }

        private Cache _cache;
        private Cache Cache
        {
            get
            {
                if (_cache == null)
                {
                    if (HttpContext.Current == null)
                        _cache = HttpRuntime.Cache;
                    else
                        _cache = HttpContext.Current.Cache;
                }

                return _cache;
            }
        }
        private CircuitBreakerState state;
        private Exception lastExecutionException = null;

        public int Failures
        {
            get
            {
                return CircuitBreakerExceptionCounts.GetOrAdd(CircuitId, 0);
            }
            set
            {
                CircuitBreakerExceptionCounts.AddOrUpdate(CircuitId, value, (key, oldValue) => value);
            }
        }
        public int Threshold { get; private set; }
        public TimeSpan OpenTimeout { get; private set; }

        public string CacheKey { get; private set; }
        public CacheDependency CacheDependency { get; private set; }
        public TimeSpan CacheDuration { get; private set; }
        public TimeSpan CacheSlidingExpiration { get; private set; }
        public CacheItemPriority CacheItemPriority { get; private set; }

        public bool FileBacked
        {
            get
            {
                return !string.IsNullOrWhiteSpace(WorkingDirectory);
            }
        }
        public string FileName { get; private set; }
        public string WorkingDirectory { get; private set; }
        public bool FallbackToStaleFileCache { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="circuitId">The ID of the Circuit - this can be the same or different than the CacheKey.</param>
        /// <param name="cacheKey">The unique CacheKey of this request.</param>
        /// <param name="cacheDependency">An optional CacheDependency.</param>
        /// <param name="cacheDuration">The duration you'd like to keep in Cache - this applies both to in-memory and file backed.</param>
        /// <param name="cacheSlidingExpiration">Optionally set a sliding expiration - to refresh the cache when if it hasn't been accessed in a period of time.</param>
        /// <param name="cacheItemPriority">Optionally set a cache priority to manage memory - default is 'Normal'</param>
        /// <param name="workingDirectory">If set, enables File based cache to handle cases where object is kicked out of cache before the CacheDuration has expired. Expects an absolute path to a folder i.e. C:/Cache.</param>
        /// <param name="failureThreshold">Optionally override the failure. Default is 3.</param>
        /// <param name="openTimeout">Optionally override the OpenTimeout. Default is 5 seconds.</param>
        /// <param name="fallbackToStaleFileCache">Optionally override the default behaviour of falling back to old file based cache if the function continues to fail. If file based cache is disabled, this setting is ignored.</param>
        public CircuitBreaker(
            string circuitId,
            string cacheKey = null,
            CacheDependency cacheDependency = null,
            TimeSpan? cacheDuration = null,
            TimeSpan? cacheSlidingExpiration = null,
            CacheItemPriority cacheItemPriority = CacheItemPriority.Normal,
            string workingDirectory = null,
            int failureThreshold = 3,
            TimeSpan? openTimeout = null,
            bool fallbackToStaleFileCache = true
            )
        {
            if (cacheDuration == null)
                cacheDuration = TimeSpan.FromMinutes(5);

            if (cacheDuration == TimeSpan.Zero)
                throw new ArgumentNullException("cacheDuration", "You must specify a cache duration greater than 0");

            if (cacheSlidingExpiration == null)
                cacheSlidingExpiration = Cache.NoSlidingExpiration;

            if (openTimeout == null)
                openTimeout = TimeSpan.FromSeconds(5);

            CircuitId = circuitId;

            Threshold = failureThreshold;
            OpenTimeout = (TimeSpan)openTimeout;

            CacheKey = cacheKey;
            CacheDependency = cacheDependency;
            CacheDuration = (TimeSpan)cacheDuration;
            CacheSlidingExpiration = (TimeSpan)cacheSlidingExpiration;
            CacheItemPriority = cacheItemPriority;

            WorkingDirectory = workingDirectory;

            if (FileBacked)
            {
                FileName = GetFileNameFromCacheKey();
            } else
            {
                fallbackToStaleFileCache = false;
            }

            FallbackToStaleFileCache = fallbackToStaleFileCache;

            //Initialize
            MoveToClosedState();
        }


        /// <summary>
        /// Executes a specified Func<T> within the confines of the Circuit Breaker Pattern (https://msdn.microsoft.com/en-us/library/dn589784.aspx)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="funcToIvoke"></param>
        /// <returns>Object of type T of default(T)</returns>
        public T Execute<T>(Func<T> funcToInvoke)
        {
            object circuitBreakerLock = CircuitBreakLock;

            T resp = default(T);
            this.lastExecutionException = null;

            #region Initiation Execution
            lock (circuitBreakerLock)
            {
                state.ExecutionStart();
                if (state is OpenState)
                {
                    return resp; //Stop execution of this method
                }
            }
            #endregion

            #region Do the work
            try
            {
                //Access Without Cache
                if (String.IsNullOrWhiteSpace(FileName))
                {
                    lock (circuitBreakerLock)
                    {
                        //do the work
                        resp = funcToInvoke();
                    }
                }
                else
                {
                    //check mem cache
                    if (!ReadFromCache<T>(out resp))
                    {
                        lock (circuitBreakerLock)
                        {
                            if (!ReadFromCache<T>(out resp))
                            {
                                if (FileBacked)
                                {
                                    //check file system
                                    if (!ReadFromFile<T>(out resp))
                                    {
                                        //do the work
                                        resp = funcToInvoke();

                                        AddToCache(resp);
                                        WriteToFile(resp);
                                    }
                                    else
                                    {
                                        //read from file system is "fresh", pump into mem cache
                                        AddToCache(resp);
                                    }
                                }
                                else
                                {
                                    //do the work
                                    resp = funcToInvoke();

                                    AddToCache(resp);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {

                lock (circuitBreakerLock)
                {
                    lastExecutionException = e;
                    state.ExecutionFail(e);
                }

                if (FallbackToStaleFileCache)
                {
                    //try to get stale content from FileSystem
                    if (ReadFromFile<T>(out resp, true))
                    {
                        //cache the old response in memory
                        AddToCache(resp);

                        return resp;
                    }
                }

                throw;
            }
            finally
            {
                lock (circuitBreakerLock)
                {
                    state.ExecutionComplete();
                }
            }
            #endregion

            return resp;
        }

        #region State Management
        public bool IsClosed
        {
            get { return state.Update() is ClosedState; }
        }

        public bool IsOpen
        {
            get { return state.Update() is OpenState; }
        }

        public bool IsHalfOpen
        {
            get { return state.Update() is HalfOpenState; }
        }

        public bool IsThresholdReached()
        {
            return Failures >= Threshold;
        }

        public Exception GetLastExecutionException()
        {
            return lastExecutionException;
        }

        void Close()
        {
            MoveToClosedState();
        }

        void Open()
        {
            MoveToOpenState();
        }

        internal CircuitBreakerState MoveToClosedState()
        {
            state = new ClosedState(this);
            return state;
        }

        internal CircuitBreakerState MoveToOpenState()
        {
            state = new OpenState(this);
            return state;
        }

        internal CircuitBreakerState MoveToHalfOpenState()
        {
            state = new HalfOpenState(this);
            return state;
        }

        internal void IncreaseFailureCount()
        {
            Failures++;
        }

        internal void ResetFailureCount()
        {
            Failures = 0;
        }
        #endregion

        #region Caching
        internal bool ReadFromCache<T>(out T resp)
        {
            bool success = true;
            var res = Cache[CacheKey];

            if (res is T)
                resp = (T)res;
            else
            {
                success = false;
                resp = default(T);
            }

            return success;
        }

        internal void AddToCache<T>(T obj)
        {
            if (obj != null)
            {
                Cache.Remove(CacheKey);
                Cache.Add(CacheKey, obj, CacheDependency, DateTime.Now.Add(CacheDuration), CacheSlidingExpiration, CacheItemPriority, null);
            }
        }
        #endregion

        #region File Management
        internal bool ReadFromFile<T>(out T resp, bool ignoreCacheDuration = false)
        {
            bool success = false;
            resp = default(T);
            string path = ResolveFilePath();

            if (!string.IsNullOrWhiteSpace(path))
            {
                FileInfo fi = new FileInfo(path);

                if (fi.Exists)
                {

                    //if this file is "too old" delete and we need to respect the cache duration
                    if ((DateTime.UtcNow - fi.LastWriteTimeUtc > CacheDuration) && !ignoreCacheDuration)
                    {
                        if (!FallbackToStaleFileCache)
                        {
                            //clean up old file if we'll never fallback to it
                            fi.Delete();
                        }
                    }
                    //file is still fresh enough (or we've ignored the cache duration), read and pump into mem cahce
                    else
                    {
                        string res = File.ReadAllText(path);
                        resp = JsonConvert.DeserializeObject<T>(res);
                        success = true;
                    }

                }
            }

            return success;
        }

        internal void WriteToFile<T>(T objToWrite)
        {
            if (!FileBacked)
                return;

            if (objToWrite == null)
                return;

            string objStr = JsonConvert.SerializeObject(objToWrite);
            string path = ResolveFilePath();

            if (!string.IsNullOrWhiteSpace(path))
            {
                FileInfo fi = new FileInfo(path);

                if (fi.Exists)
                    fi.Delete();

                File.WriteAllText(path, objStr);

                //write to object cache
                AddToCache(objToWrite);
            }

        }

        internal string SerializeObject<T>(T objToSerialize)
        {
            string objStr = null;

            if (objToSerialize is DataTable)
                objStr = JsonConvert.SerializeObject(objToSerialize, new DataTableConverter());
            else if (objToSerialize is DataSet)
                objStr = JsonConvert.SerializeObject(objToSerialize, new DataSetConverter());
            else
                objStr = JsonConvert.SerializeObject(objToSerialize);

            return objStr;
        }

        internal string GetFileNameFromCacheKey()
        {
            if (string.IsNullOrEmpty(CacheKey)) throw new ArgumentNullException("CacheKey", "A cache key must be specified");

            string str = CacheKey.ToLower();

            str = Regex.Replace(str, @"&\w+;", "");
            str = Regex.Replace(str, @"[^a-z0-9\-\s]", "", RegexOptions.IgnoreCase);
            str = str.Replace(" ", "-");
            str = Regex.Replace(str, @"-{2,}", "-");

            return str;
        }

        internal string ResolveFilePath()
        {
            if (!Directory.Exists(WorkingDirectory))
                Directory.CreateDirectory(WorkingDirectory);

            string fileName = FileName;
            //check if there's a trailing slash, if not add
            if (WorkingDirectory.Substring(WorkingDirectory.Length - 1) != "/")
            {
                fileName = String.Format("/{0}.json", fileName);
            }

            return String.Format("{0}{1}", WorkingDirectory, fileName);
        }
        #endregion
    }

    public abstract class CircuitBreakerState
    {
        protected readonly CircuitBreaker circuitBreaker;

        protected CircuitBreakerState(CircuitBreaker circuitBreaker)
        {
            this.circuitBreaker = circuitBreaker;
        }

        public virtual CircuitBreaker ExecutionStart()
        {
            return this.circuitBreaker;
        }
        public virtual void ExecutionComplete() { }
        public virtual void ExecutionFail(Exception e) { circuitBreaker.IncreaseFailureCount(); }

        public virtual CircuitBreakerState Update()
        {
            return this;
        }
    }

    public class OpenState : CircuitBreakerState
    {
        private readonly DateTime openDateTime; //last time something went wrong, or breaker was initialized
        public OpenState(CircuitBreaker circuitBreaker)
            : base(circuitBreaker)
        {
            //initialize openDateTime
            openDateTime = DateTime.UtcNow;
        }

        public override CircuitBreaker ExecutionStart()
        {
            //kickoff execution
            base.ExecutionStart();
            this.Update();
            return base.circuitBreaker;
        }

        public override CircuitBreakerState Update()
        {
            base.Update();

            if (DateTime.UtcNow >= openDateTime + base.circuitBreaker.OpenTimeout)
            {
                //timeout has passed, progress state to "half-open"
                return circuitBreaker.MoveToHalfOpenState();
            }

            return this;
        }
    }

    public class HalfOpenState : CircuitBreakerState
    {
        public HalfOpenState(CircuitBreaker circuitBreaker) : base(circuitBreaker) { }

        public override void ExecutionFail(Exception e)
        {
            //FAIL, set back to "open"
            base.ExecutionFail(e);
            circuitBreaker.MoveToOpenState();
        }

        public override void ExecutionComplete()
        {
            //SUCCESS, set to "closed"
            base.ExecutionComplete();
            circuitBreaker.MoveToClosedState();
        }
    }

    public class ClosedState : CircuitBreakerState
    {
        public ClosedState(CircuitBreaker circuitBreaker)
            : base(circuitBreaker)
        {
            //Reset fail count as soon as we have a success
            circuitBreaker.ResetFailureCount();
        }

        public override void ExecutionFail(Exception e)
        {
            base.ExecutionFail(e);
            if (circuitBreaker.IsThresholdReached())
            {
                //if we've reached the specified fail threshold, set to "open state"
                circuitBreaker.MoveToOpenState();
            }
        }
    }
}
