using System.Buffers.Text;
using System.Collections.Concurrent;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;

namespace Sofar.LoggerManager
{
    public sealed class LoggerManager : IDisposable
    {
        #region Dispoable Singleton

        private static readonly Lazy<LoggerManager> _lazySingleton = new(() => new LoggerManager());

        public static LoggerManager Instance => _lazySingleton.Value;

        private LoggerManager()
        {
            Log.Logger = CreateFileLogger("Default", RollingInterval.Infinite, true);
        }

        private void ReleaseUnmanagedResources()
        {
            foreach (var iLogger in _loggersDict.Values)
            {
                (iLogger as Serilog.Core.Logger)?.Dispose();
            }
            Log.CloseAndFlush();
            _loggersDict.Clear();
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~LoggerManager()
        {
            ReleaseUnmanagedResources();
        }

        #endregion


        private ConcurrentDictionary<string, ILogger> _loggersDict = new();

        private string _baseFolder = "./Logs";

        public ILogger CreateFileLogger(string name, RollingInterval rollingInterval = RollingInterval.Day, bool splitLevelFiles = false)
        {
            if (_loggersDict.ContainsKey(name))
            {
                throw new InvalidOperationException($"The logger named \"{name}\" already exists. Use the \"GetLogger\" method to access the logger.");
            }

            string filepath;
            string levelPlaceholder = splitLevelFiles ? "_Level" : "";
            if (rollingInterval == RollingInterval.Infinite)
            {
                filepath = Path.Combine(_baseFolder, $"{name}/{name}_{DateTime.Now.ToString("yyMMdd_HHmmss")}{levelPlaceholder}.log");
            }
            else
            {
                filepath = Path.Combine(_baseFolder, $"{name}/{name}{levelPlaceholder}_.log");
            }

            string logTemplate =
                "[{Timestamp:yy/MM/dd HH:mm:ss.fff zzz}] [{Level:u3}] [Th/Pg:{ThreadId}/{ProcessId}] {Message:lj}{NewLine}{Exception}";


            ILogger logger;
            var baseCfg = new LoggerConfiguration()
                                                .MinimumLevel.Debug()
                                                .Enrich.WithExceptionDetails()
                                                .Enrich.WithProcessId()
                                                .Enrich.WithThreadId();
            if (splitLevelFiles)
            {
                logger = baseCfg
                        .WriteTo.Async(lsc =>
                        {
                            lsc.Conditional(
                                logEvent => logEvent.Level == LogEventLevel.Error || logEvent.Level == LogEventLevel.Fatal,
                                sinkCfg =>
                                {
                                    sinkCfg.File(path: filepath.Replace(levelPlaceholder, "_Error"), 
                                        rollingInterval: rollingInterval, outputTemplate: logTemplate);
                                });
                            lsc.Conditional(
                                logEvent => logEvent.Level == LogEventLevel.Information || logEvent.Level == LogEventLevel.Warning,
                                sinkCfg =>
                                {
                                    sinkCfg.File(path: filepath.Replace(levelPlaceholder, "_Info"),
                                        rollingInterval: rollingInterval, outputTemplate: logTemplate);
                                }); 
                            lsc.Conditional(
                                logEvent => logEvent.Level == LogEventLevel.Debug || logEvent.Level == LogEventLevel.Verbose,
                                sinkCfg =>
                                {
                                    sinkCfg.File(path: filepath.Replace(levelPlaceholder, "_Dbg"),
                                        rollingInterval: rollingInterval, outputTemplate: logTemplate);
                                });

                        })
                        .CreateLogger();
            }
            else
            {
                logger = baseCfg
                        .WriteTo.Async(lsc =>
                        {
                            lsc.File(path: filepath, rollingInterval: rollingInterval, outputTemplate: logTemplate);

                        })
                        .CreateLogger();
            }
        

            return _loggersDict.GetOrAdd(name, logger);
        }


        public ILogger DefaultLogger => Log.Logger;
        

        public ILogger? GetLogger(string name)
        {
            if (_loggersDict.TryGetValue(name, out var logger))
                return logger;
            

            return null;
        }

        public string[] GetAvailableLoggers()
        {
            return _loggersDict.Keys.ToArray();
        }

       
    }

    // public enum LogInterval
    // {
    //     Infinite = RollingInterval.Infinite,
    //    
    //     Year = RollingInterval.Year,
    //
    //     Month = RollingInterval.Month,
    //
    //     Day = RollingInterval.Day,
    //     
    //     Hour = RollingInterval.Hour,
    //
    //     Minute = RollingInterval.Minute,
    // }
}
