// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Benchmarks.ClientJob;
using Newtonsoft.Json;

namespace BenchmarksClient.Workers
{
    public class Wrk2Worker : IWorker
    {
        private static TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(5);
        private static TimeSpan LatencyTimeout = TimeSpan.FromSeconds(2);

        private static HttpClient _httpClient;
        private static HttpClientHandler _httpClientHandler;

        private ClientJob _job;
        private Process _process;

        public string JobLogText { get; set; }

        static Wrk2Worker()
        {
            // Configuring the http client to trust the self-signed certificate
            _httpClientHandler = new HttpClientHandler();
            _httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            _httpClientHandler.MaxConnectionsPerServer = 1;
            _httpClient = new HttpClient(_httpClientHandler);
        }

        private void InitializeJob()
        {
            _job.ClientProperties.TryGetValue("ScriptName", out var scriptName);

            if (_job.ClientProperties.TryGetValue("PipelineDepth", out var pipelineDepth))
            {
                Debug.Assert(int.Parse(pipelineDepth) <= 0 || scriptName != null, "A script name must be present when the pipeline depth is larger than 0.");
            }

            var jobLogText =
                        $"[ID:{_job.Id} Connections:{_job.Connections} Threads:{_job.Threads} Duration:{_job.Duration} Method:{_job.Method} ServerUrl:{_job.ServerBenchmarkUri}";

            if (!string.IsNullOrEmpty(scriptName))
            {
                jobLogText += $" Script:{scriptName}";
            }

            if (pipelineDepth != null && int.Parse(pipelineDepth) > 0)
            {
                jobLogText += $" Pipeline:{pipelineDepth}";
            }

            if (_job.Headers != null)
            {
                jobLogText += $" Headers:{JsonConvert.SerializeObject(_job.Headers)}";
            }

            jobLogText += "]";

            JobLogText = jobLogText;
        }

        public async Task StartJobAsync(ClientJob job)
        {
            _job = job;
            InitializeJob();

            await MeasureFirstRequestLatencyAsync(_job);

            _job.State = ClientState.Running;
            _job.LastDriverCommunicationUtc = DateTime.UtcNow;

            _process = StartProcess(_job);
        }

        public Task StopJobAsync()
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _process.Dispose();
            _process = null;
        }
        private static HttpRequestMessage CreateHttpMessage(ClientJob job)
        {
            var requestMessage = new HttpRequestMessage(new HttpMethod(job.Method), job.ServerBenchmarkUri);

            foreach (var header in job.Headers)
            {
                requestMessage.Headers.Add(header.Key, header.Value);
            }

            return requestMessage;
        }

        private static async Task MeasureFirstRequestLatencyAsync(ClientJob job)
        {
            if (job.SkipStartupLatencies)
            {
                return;
            }

            Log($"Measuring first request latency on {job.ServerBenchmarkUri}");

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            using (var message = CreateHttpMessage(job))
            {
                var cts = new CancellationTokenSource();
                var token = cts.Token;
                cts.CancelAfter(FirstRequestTimeout);
                token.ThrowIfCancellationRequested();

                try
                {
                    using (var response = await _httpClient.SendAsync(message, token))
                    {
                        job.LatencyFirstRequest = stopwatch.Elapsed;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log("A timeout occurred while measuring the first request: " + FirstRequestTimeout.ToString());
                }
                finally
                {
                    cts.Dispose();
                }
            }

            Log($"{job.LatencyFirstRequest.TotalMilliseconds} ms");

            Log("Measuring subsequent requests latency");

            for (var i = 0; i < 10; i++)
            {
                stopwatch.Restart();

                using (var message = CreateHttpMessage(job))
                {
                    var cts = new CancellationTokenSource();
                    var token = cts.Token;
                    cts.CancelAfter(LatencyTimeout);
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        using (var response = await _httpClient.SendAsync(message))
                        {
                            // We keep the last measure to simulate a warmup phase.
                            job.LatencyNoLoad = stopwatch.Elapsed;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("A timeout occurred while measuring the latency, skipping ...");
                        break;
                    }
                    finally
                    {
                        cts.Dispose();
                    }
                }
            }

            Log($"{job.LatencyNoLoad.TotalMilliseconds} ms");
        }

        private static Process StartProcess(ClientJob job)
        {

            var customScripts = new List<string>();

            // Copying custom scripts
            foreach(var script in job.Attachments)
            {
                if (!Directory.Exists("scripts/custom"))
                {
                    Directory.CreateDirectory("scripts/custom");
                }

                Log("Copying script: " + script.Filename);

                var destination = Path.Combine(Path.GetDirectoryName(typeof(WrkWorker).GetTypeInfo().Assembly.Location), "scripts/custom/" + script.Filename);

                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Copy(script.TempFilename, destination, true);

                File.Delete(script.TempFilename);

                customScripts.Add("scripts/custom/" + script.Filename);
            }

            var command = "wrk2";

            if (job.Headers != null)
            {
                foreach (var header in job.Headers)
                {
                    command += $" -H \"{header.Key}: {header.Value}\"";
                }
            }

            command += $" --latency -d {job.Duration} -c {job.Connections} --timeout {job.Timeout} -t {job.Threads}  {job.ServerBenchmarkUri}{job.Query}";

            if (job.ClientProperties.TryGetValue("rate", out var rate) && !string.IsNullOrEmpty(rate))
            {
                command += $" --rate {rate}";
            }

            foreach (var customScript in customScripts)
            {
                command += $" -s {customScript}";
            }

            if (job.ClientProperties.TryGetValue("ScriptName", out var scriptName) && !string.IsNullOrEmpty(scriptName))
            {
                command += $" -s scripts/{scriptName}.lua --";

                var pipeLineDepth = int.Parse(job.ClientProperties["PipelineDepth"]);
                if (pipeLineDepth > 0)
                {
                    command += $" {pipeLineDepth}";
                }
            }

            Log(command);

            var process = new Process()
            {
                StartInfo = {
                    FileName = "stdbuf",
                    Arguments = $"-oL {command}",
                    WorkingDirectory = Path.GetDirectoryName(typeof(WrkWorker).GetTypeInfo().Assembly.Location),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    Log(e.Data);
                    job.Output += (e.Data + Environment.NewLine);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    Log(e.Data);
                    job.Error += (e.Data + Environment.NewLine);
                }
            };

            process.Exited += (_, __) =>
            {
                // Wait for all Output messages to be flushed and available in job.Output
                Thread.Sleep(100);

                var rpsMatch = Regex.Match(job.Output, @"Requests/sec:\s*([\d\.]*)");
                if (rpsMatch.Success && rpsMatch.Groups.Count == 2)
                {
                    job.RequestsPerSecond = double.Parse(rpsMatch.Groups[1].Value);
                }

                const string LatencyPattern = @"\s+{0}\s+([\d\.]+)(\w+)";

                var latencyMatch = Regex.Match(job.Output, String.Format(LatencyPattern, "Latency"));
                job.Latency.Average = ReadLatency(latencyMatch);

                var p50Match = Regex.Match(job.Output, String.Format(LatencyPattern, "50\\.000%"));
                job.Latency.Within50thPercentile = ReadLatency(p50Match);

                var p75Match = Regex.Match(job.Output, String.Format(LatencyPattern, "75\\.000%"));
                job.Latency.Within75thPercentile = ReadLatency(p75Match);

                var p90Match = Regex.Match(job.Output, String.Format(LatencyPattern, "90\\.000%"));
                job.Latency.Within90thPercentile = ReadLatency(p90Match);

                var p99Match = Regex.Match(job.Output, String.Format(LatencyPattern, "99\\.000%"));
                job.Latency.Within99thPercentile = ReadLatency(p99Match);

                var p100Match = Regex.Match(job.Output, String.Format(LatencyPattern, "100\\.000%"));
                job.Latency.MaxLatency = ReadLatency(p100Match);

                var socketErrorsMatch = Regex.Match(job.Output, @"Socket errors: connect ([\d\.]*), read ([\d\.]*), write ([\d\.]*), timeout ([\d\.]*)");
                job.SocketErrors = CountSocketErrors(socketErrorsMatch);

                var badResponsesMatch = Regex.Match(job.Output, @"Non-2xx or 3xx responses: ([\d\.]*)");
                job.BadResponses = ReadBadReponses(badResponsesMatch);

                var requestsCountMatch = Regex.Match(job.Output, @"([\d\.]*) requests in ([\d\.]*)(\w*)");
                job.Requests = ReadRequests(requestsCountMatch);
                job.ActualDuration = ReadDuration(requestsCountMatch);

                job.State = ClientState.Completed;
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }

        private static TimeSpan ReadDuration(Match responseCountMatch)
        {
            if (!responseCountMatch.Success || responseCountMatch.Groups.Count != 4)
            {
                Log("Failed to parse duration");
                return TimeSpan.Zero;
            }

            try
            {
                var value = double.Parse(responseCountMatch.Groups[2].Value);

                var unit = responseCountMatch.Groups[3].Value;

                switch (unit)
                {
                    case "s": return TimeSpan.FromSeconds(value);
                    case "m": return TimeSpan.FromMinutes(value);
                    case "h": return TimeSpan.FromHours(value);

                    default: throw new NotSupportedException("Failed to parse duration unit: " + unit);
                }
            }
            catch
            {
                Log("Failed to parse durations");
                return TimeSpan.Zero;
            }
        }

        private static int ReadRequests(Match responseCountMatch)
        {
            if (!responseCountMatch.Success || responseCountMatch.Groups.Count != 4)
            {
                Log("Failed to parse requests");
                return -1;
            }

            try
            {
                return int.Parse(responseCountMatch.Groups[1].Value);
            }
            catch
            {
                Log("Failed to parse requests");
                return -1;
            }
        }

        private static int ReadBadReponses(Match badResponsesMatch)
        {
            if (!badResponsesMatch.Success)
            {
                // wrk does not display the expected line when no bad responses occur
                return 0;
            }

            if (!badResponsesMatch.Success || badResponsesMatch.Groups.Count != 2)
            {
                Log("Failed to parse bad responses");
                return 0;
            }

            try
            {
                return int.Parse(badResponsesMatch.Groups[1].Value);
            }
            catch
            {
                Log("Failed to parse bad responses");
                return 0;
            }
        }

        private static int CountSocketErrors(Match socketErrorsMatch)
        {
            if (!socketErrorsMatch.Success)
            {
                // wrk does not display the expected line when no errors occur
                return 0;
            }

            if (socketErrorsMatch.Groups.Count != 5)
            {
                Log("Failed to parse socket errors");
                return 0;
            }

            try
            {
                return
                    int.Parse(socketErrorsMatch.Groups[1].Value) +
                    int.Parse(socketErrorsMatch.Groups[2].Value) +
                    int.Parse(socketErrorsMatch.Groups[3].Value) +
                    int.Parse(socketErrorsMatch.Groups[4].Value)
                    ;

            }
            catch
            {
                Log("Failed to parse socket errors");
                return 0;
            }

        }

        private static double ReadLatency(Match match)
        {
            if (!match.Success || match.Groups.Count != 3)
            {
                Log("Failed to parse latency");
                return -1;
            }

            try
            {
                var value = double.Parse(match.Groups[1].Value);
                var unit = match.Groups[2].Value;

                switch (unit)
                {
                    case "s": return value * 1000;
                    case "ms": return value;
                    case "us": return value / 1000;

                    default:
                        Log("Failed to parse latency unit: " + unit);
                        return -1;
                }
            }
            catch
            {
                Log("Failed to parse latency");
                return -1;
            }
        }

        private static void Log(string message)
        {
            var time = DateTime.Now.ToString("hh:mm:ss.fff");
            Console.WriteLine($"[{time}] {message}");
        }

        public Task DisposeAsync()
        {
            if (_process != null)
            {
                _process.Dispose();
                _process = null;
            }

            return Task.CompletedTask;
        }
    }
}