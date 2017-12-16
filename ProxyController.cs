using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HttpProxyInfo
{
    [Route("proxy")]
    public class ProxyController : Controller
    {
        private readonly HttpClient _proxylessClient;
        private readonly ILogger<ProxyController> _logger;
        private readonly Func<string, HttpClient> _httpClientFactory;

        public ProxyController(Func<string, HttpClient> httpClientFactory, HttpClient proxylessClient, ILogger<ProxyController> logger)
        {
            _proxylessClient = proxylessClient ?? throw new ArgumentNullException(nameof(proxylessClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        /// <summary>
        /// Retrieves information about a proxy server
        /// </summary>
        /// <remarks>
        /// GET /proxy/info/hostname:8080
        /// </remarks>
        /// <param name="proxy">
        /// The proxy server to get information about.
        /// Example: proxy.lan:8080
        /// Example: 192.168.1.2:8118
        /// </param>
        /// <returns>Information about the proxy server.</returns>
        [HttpGet("info/{proxy}")]
        [ProducesResponseType(typeof(ProxyInfoModel), 200)]
        public async Task<IActionResult> GetInfo([FromRoute] string proxy)
        {
            if (proxy == null)
            {
                return BadRequest("Expected Proxy with format: 'info/hostname:port' or 'info/ip:port'");
            }

            using (_logger.BeginScope(proxy))
            {
                var proxyClient = _httpClientFactory(proxy);

                var proxyPublicIpTask = VerifyPublicIpAddress(proxyClient);
                var proxyCountryTask = proxyPublicIpTask.ContinueWith(t => VerifyCountry(t.Result.IpAddress, proxyClient));
                var serverPublicIpTask = VerifyPublicIpAddress(_proxylessClient);
                var serverCountryTask = serverPublicIpTask.ContinueWith(t => VerifyCountry(t.Result.IpAddress, proxyClient));
                var proxyLatencyTask = VerifyLatency(proxy.Split(":")[0]);

                _logger.LogDebug("Running verifications for Proxy: {proxy}", proxy);

                await Task.WhenAll(proxyCountryTask, serverCountryTask, proxyLatencyTask);
                await Task.WhenAll(proxyCountryTask.Result, serverCountryTask.Result);

                return new ObjectResult(new ProxyInfoModel
                {
                    LatencyToProxy = proxyLatencyTask.Result,
                    ProxyPublicIp = proxyPublicIpTask.Result,
                    ProxyCountry = proxyCountryTask.Result.Result,
                    ServerPublicIp = serverPublicIpTask.Result,
                    ServerCountry = serverCountryTask.Result.Result
                })
                {
                    StatusCode = 200
                };
            }
        }

        private static async Task<TVerification> Verify<TVerification, TOpResult>(
            Func<Task<TOpResult>> operation,
            Action<TVerification> handleError,
            Action<TVerification, TOpResult> handleSuccess)
            where TVerification : VerificationResult, new()
        {
            var sw = new Stopwatch();
            var result = new TVerification();

            try
            {
                sw.Start();
                var opResult = await operation().ConfigureAwait(false);
                sw.Stop();
                result.Latency = sw.Elapsed;
                handleSuccess(result, opResult);
            }
            catch (Exception e)
            {
                sw.Stop();
                result.Latency = sw.Elapsed;
                result.Error = e;
                handleError(result);
            }

            return result;
        }

        // Get the name of the country where the IP address is allocated using an external service.
        private Task<CountryVerificationResult> VerifyCountry(IPAddress ipAddress, HttpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            return ipAddress == null
                ? Task.FromResult<CountryVerificationResult>(null)
                : Verify<CountryVerificationResult, string>(Operation, HandleError, HandleSuccess);

            void HandleError(CountryVerificationResult r)
                => _logger.LogError(r.Error, "Failed to resolve Country of IP '{ipAddress}'. Elapsed: {latency}", ipAddress, r.Latency);

            Task<string> Operation()
                => client.GetStringAsync($"https://ipinfo.io/{ipAddress}/country");

            void HandleSuccess(CountryVerificationResult result, string ipinfoResponse)
                => result.Country = Regex.Replace(ipinfoResponse, "[^0-9a-zA-Z]+", string.Empty);
        }

        // Verifies the latency to the host using ICMP echo (ping)
        private Task<VerificationResult> VerifyLatency(string host)
        {
            return host == null
                ? Task.FromResult<VerificationResult>(null)
                : Verify<VerificationResult, PingReply>(Operation, HandleError, HandleSuccess);

            void HandleError(VerificationResult r)
                => _logger.LogError(r.Error, "Failed to reach the Proxy server via ICMP Echo. Elapsed: {latency}", r.Latency);

            async Task<PingReply> Operation()
            {
                using (var ping = new Ping())
                    return await ping.SendPingAsync(host, 1000).ConfigureAwait(false);
            }

            void HandleSuccess(VerificationResult result, PingReply pong)
            {
                var pingRoundtripTime = TimeSpan.FromMilliseconds(pong.RoundtripTime);

                if (pong.Status != IPStatus.Success)
                {
                    result.Error = new Exception(pong.Status.ToString());
                }
                else
                {
                    result.Latency = pingRoundtripTime;
                }

                _logger.LogTrace("Ping status: '{status}'. Took '{latency}' while the overall verification took '{echoOverhead}'.",
                    pong.Status, pingRoundtripTime, result.Latency);
            }
        }

        // Uses an external service to retrieve the public IP address using the provided HttpClient
        private Task<PublicIpVerificationResult> VerifyPublicIpAddress(HttpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            return Verify<PublicIpVerificationResult, string>(Operation, HandleError, HandleSuccess);

            void HandleError(PublicIpVerificationResult r)
                => LogError(r.Error);

            Task<string> Operation()
                => client.GetStringAsync("https://api.ipify.org");

            void HandleSuccess(PublicIpVerificationResult result, string response)
            {
                if (IPAddress.TryParse(response, out var externalIp))
                {
                    result.IpAddress = externalIp;
                }
                else
                {
                    result.Error = new Exception($"Not able to parse response '{response}' to IP Address.");
                    LogError(result.Error);
                }
            }

            void LogError(Exception error) => _logger.LogError(error, "Failed to retrieve external IP.");
        }

        private class PublicIpVerificationResult : VerificationResult
        {
            [JsonIgnore]
            public IPAddress IpAddress { get; set; }
            public string Ip => IpAddress?.ToString();
        }

        private class CountryVerificationResult : VerificationResult
        {
            public string Country { get; set; }
        }

        public class VerificationResult
        {
            public TimeSpan Latency { get; set; }
            [JsonIgnore]
            public Exception Error { get; set; }
            public string ErrorMessage => Error?.Message;
        }

        private class ProxyInfoModel
        {
            public VerificationResult LatencyToProxy { get; set; }
            public PublicIpVerificationResult ProxyPublicIp { get; set; }
            public CountryVerificationResult ProxyCountry { get; set; }
            public PublicIpVerificationResult ServerPublicIp { get; set; }
            public CountryVerificationResult ServerCountry { get; set; }
        }
    }
}
