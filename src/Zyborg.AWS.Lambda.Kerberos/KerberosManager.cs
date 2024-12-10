using DnsClient;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Zyborg.AWS.Lambda.Kerberos
{
    public class KerberosManager
    {
        private static readonly ILogger DefaultConsoleLogger =
            new Logging.SimpleConsoleLogger(typeof(KerberosManager).FullName);

        /// <summary>
        /// This key will be searched for in the current set of process environment
        /// variables to determine if we are running in the context of a Lambda runtime.
        /// </summary>
        public const string AwsLambdaFuncNameEnvKey = "AWS_LAMBDA_FUNCTION_NAME";

        public const string LambdaWriteDir = "/tmp";
        public const string LambdaTaskDir = "/var/task";

        public const string LocalBinDir = LambdaTaskDir + "/local";
        public const string LocalLibDir = LambdaTaskDir + "/lib";
        public const string LocalEtcDir = LambdaTaskDir + "/etc";

        public const string KinitPath = LocalBinDir + "/kinit";
        public const string KlistPath = LocalBinDir + "/klist";

        public const string Krb5ConfigSource = LocalEtcDir + "/lambda-krb5.conf";
        public const string Krb5ConfigTarget = LambdaWriteDir + "/lambda-krb.conf";
        public const string Krb5KeyTabTarget = LambdaWriteDir + "/lambda.keytab";
        public const string Krb5CCacheTarget = LambdaWriteDir + "/lambda.ccache";

        public const string Krb5ConfigEnvKey = "KRB5_CONFIG";

        private ILogger _logger;

        private bool _isLinux;
        private string _awsLambdaFuncName;

        private DateTime _lastKinit = DateTime.MinValue;
        private object _lastKinitLock = new object();

        private string _kinitArgs;
        private ProcessStartInfo _kinitStartInfo;

        public KerberosManager(KerberosOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            Options = options;
            _logger = options.Logger;
            if (_logger == null)
            {
                // Default-ish behavior pre-Logger
                _logger = DefaultConsoleLogger;
                _logger.LogWarning("No logger specified, defaulting to simple console logging");
            }

            _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            _awsLambdaFuncName = Environment.GetEnvironmentVariable(AwsLambdaFuncNameEnvKey);

            Enabled = _isLinux && !string.IsNullOrEmpty(_awsLambdaFuncName);

            _logger.LogInformation($"Kerberos Manager is [{(Enabled ? "ENABLED" : "DISABLED")}]:");
            _logger.LogInformation("* Is Linux: " + _isLinux);
            _logger.LogInformation("* Lambda Function Name: " + _awsLambdaFuncName);

            // DEBUG:
            // foreach (var env in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().OrderBy(e => e.Key))
            // {
            //     _logger.LogInformation($"ENV: [{env.Key}]=[{env.Value}]");
            // }
        }

        /// <summary>
        /// Indicates if the Kerberose Manager is enabled and will perform any management
        /// of Kerberos.  For this to resolve to true, the Manager must resolve that the
        /// running environment is Linux and that is running within a Lambda runtime context.
        /// When disabled the Manager will perform no actions during its initialization and
        /// refresh operations (noop).
        /// </summary>
        /// <value></value>
        public bool Enabled { get; }

        internal KerberosOptions Options { get; }

        public void Init(Stream keytab)
        {
            if (!Enabled)
                return;

            _kinitArgs = $"-V -kt {Krb5KeyTabTarget} {Options.Principal}";

            _logger.LogInformation("Resolving Realm KDC");
            ResolveKdc();

            _logger.LogInformation("Persisting KRB5 configuration");
            PrepareKrb5Config();

            _logger.LogInformation("Persisting KRB5 keytab");
            using (var fs = new FileStream(Krb5KeyTabTarget, FileMode.Create))
            {
                keytab.CopyTo(fs);
            }

            _kinitStartInfo = new ProcessStartInfo()
            {
                FileName = KinitPath,
                Arguments = _kinitArgs,
                UseShellExecute = false,
            };
    
            _logger.LogInformation($"Initializing Kerberos TGT for principal [{Options.Principal}]");
            Process.Start(_kinitStartInfo).WaitForExit();
            _lastKinit = DateTime.Now;
            _logger.LogInformation($"...completed at [{_lastKinit}]");
        }

        void ResolveKdc()
        {
            if (string.IsNullOrEmpty(Options.RealmKdc) && !string.IsNullOrEmpty(Options.RealmKdcSrvName))
            {
                _logger.LogInformation($"Resolving Realm KDC from DNS SRV record [{Options.RealmKdcSrvName}]");
                var dns = new LookupClient();
                var qry = dns.Query(Options.RealmKdcSrvName, QueryType.SRV);
                if (qry.HasError)
                {
                    _logger.LogError("Failed to resolve DNS query: " + qry.ErrorMessage);
                }
                else
                {
                    var srv = qry.Answers.SrvRecords().FirstOrDefault();
                    if (srv == null)
                    {
                        _logger.LogError("DNS Query returned no SRV results");
                    }
                    else
                    {
                        _logger.LogInformation($"Resolved SRV query as [{srv.Target}]");
                        Options.RealmKdc = srv.Target.Value;
                    }
                }
            }

            if (string.IsNullOrEmpty(Options.RealmKdc))
            {
                _logger.LogWarning("Realm KDC is unspecified");
            }
            else
            {
                _logger.LogInformation($"Realm KDC resolved as [{Options.RealmKdc}]");
            }
        }

        void PrepareKrb5Config()
        {
            _logger.LogInformation("Reading in KRB5 configuration template...");
            var configSource = File.ReadAllText(Krb5ConfigSource);
            var configTarget = TemplateEvaluator.Eval(configSource, this);
            File.WriteAllText(Krb5ConfigTarget, configTarget);
            _logger.LogInformation($"...wrote out KRB5 configuration to [{Krb5ConfigTarget}]");

            // Export twice, for our benefit, as well as any spawned children
            Environment.SetEnvironmentVariable(Krb5ConfigEnvKey, Krb5ConfigTarget);
            NativeEnv.SetEnv(Krb5ConfigEnvKey, Krb5ConfigTarget);
        }

        public void Refresh(bool force = false)
        {
            if (!Enabled)
                return;

            if (force || (DateTime.Now - _lastKinit) >= Options.TicketLifetime)
            {
                lock (_lastKinitLock)
                {
                    if (force || (DateTime.Now - _lastKinit) > Options.TicketLifetime)
                    {
                        _logger.LogInformation("Kerberos TGT age as expired, regenerating...");
                        Process.Start(_kinitStartInfo).WaitForExit();
                        _lastKinit = DateTime.Now;
                        _logger.LogInformation($"...completed at [{_lastKinit}]");
                    }
                }
            }
        }
    }
}
