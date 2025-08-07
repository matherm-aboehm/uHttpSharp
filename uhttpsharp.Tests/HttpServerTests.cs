using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using uhttpsharp.Headers;
using uhttpsharp.Listeners;
using uhttpsharp.RequestProviders;

namespace uhttpsharp.Tests
{
    public class HttpServerTests
    {
        private static readonly X509Certificate SelfSignedCert;
        static HttpServerTests()
        {
            X500DistinguishedName subject = new X500DistinguishedName($"CN={Assembly.GetExecutingAssembly().GetName().Name} Certificate");
            using (ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            {
                var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection() { Oid.FromOidValue("1.3.6.1.5.5.7.3.1", OidGroup.EnhancedKeyUsage) }, false));
                //HINT: ECDsa.Create does only create a ephemeral key and CertificateRequest seems to take over
                //these key properties 1:1, so the private key is lost when used outside of this context.
                //As a workaround it can be exported and re-imported with key properties explicitly specified:
                //see: https://stackoverflow.com/a/64013730/3518520
                //https://github.com/dotnet/runtime/issues/45680
                //https://github.com/dotnet/runtime/issues/23749
                using (var sslcert = request.CreateSelfSigned(DateTime.Now, DateTime.Now.AddDays(1)))
                {
                    SelfSignedCert = new X509Certificate2(sslcert.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
                }
            }

            //HACK: It is possible that testhost(.x86).exe is compiled for TargetFrameworks lower than 4.8, but
            //we currently support only 4.8 for uhttpsharp to have some new defaults enabled.
            //For tests in this class it is required that these backwards compatible defaults are overridden
            //with actual defaults for .NET Framework 4.8
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
        }
        private static HttpServer GetTarget(IHttpRequestProvider requestProvider, out int port)
        {
            var target = new HttpServer(requestProvider);
            var listener = TcpListener.Create(0);
            target.Use(new ListenerSslDecorator(new TcpListenerAdapter(listener), SelfSignedCert));
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return target;
        }

        private static async Task<HttpWebResponse> StartRequestForTest(Uri requestUri)
        {
            var request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.Method = WebRequestMethods.Http.Get;
            request.ServerCertificateValidationCallback = delegate (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                return true;
            };
            return (HttpWebResponse)await request.GetResponseAsync();
        }

        private static async Task<HttpWebResponse> StartUploadFileForTest(Uri requestUri, FileStream fileStream, bool sendChunked)
        {
            var request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.Method = WebRequestMethods.Http.Post;
            request.ServerCertificateValidationCallback = delegate (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                return true;
            };
            string boundary = "---------------------" + DateTime.Now.Ticks.ToString("x", NumberFormatInfo.InvariantInfo);
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            string bodyContentType = "application/octet-stream";
            string postBodyHeader = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + Path.GetFileName(fileStream.Name) + "\"\r\nContent-Type: " + bodyContentType + "\r\n\r\n";
            byte[] postBodyHeaderBytes = Encoding.UTF8.GetBytes(postBodyHeader);
            string postBodyFooter = "\r\n--" + boundary + "--\r\n";
            byte[] postBodyFooterBytes = Encoding.ASCII.GetBytes(postBodyFooter);
            if (sendChunked)
                request.SendChunked = true;
            else if (fileStream.CanSeek)
                request.ContentLength = fileStream.Length + postBodyHeaderBytes.Length + postBodyFooterBytes.Length;
            using (var writeStream = await request.GetRequestStreamAsync())
            {
                await writeStream.WriteAsync(postBodyHeaderBytes, 0, postBodyHeaderBytes.Length);
                await fileStream.CopyToAsync(writeStream, 8192);
                await writeStream.WriteAsync(postBodyFooterBytes, 0, postBodyFooterBytes.Length);
            }
            return (HttpWebResponse)await request.GetResponseAsync();
        }

        private static async Task<byte[]> GetAllBytesFromStreamAsync(Stream stream)
        {
            using (var memstream = new MemoryStream())
            using (stream)
            {
                await stream.CopyToAsync(memstream);
                return memstream.ToArray();
            }
        }

        private static async Task<byte[]> GetEncodedBodyBytesForResponse(IHttpResponse response, Encoding bodyEncoding)
        {
            using (var memstream = new MemoryStream())
            using (var writer = new StreamWriter(memstream, bodyEncoding))
            {
                await response.WriteBody(writer);
                return memstream.ToArray();
            }
        }

        [Test]
        public void Should_Use_SystemDefaultTlsVersions()
        {
            // Act
            var switchFound = AppContext.TryGetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", out bool switchValue);

            // Assert
            switchFound.ShouldBe(true);
            switchValue.ShouldBe(false);
        }

        [Test]
        public async Task Should_Call_Provide_With_Right_Parameters()
        {
            // Arrange
            var mock = Substitute.For<IHttpRequestProvider>();
            using (var target = GetTarget(mock, out int port))
            {
                var requestMock = Substitute.For<IHttpRequest>();
                mock.Provide(Arg.Any<IStreamReader>()).Returns(Task.FromResult(requestMock));
                target.Use((context, next) =>
                {
                    context.Response = HttpResponse.CreateWithMessage(HttpResponseCode.Ok, "Hello World", false);
                    return Task.CompletedTask;
                });

                // Act
                target.Start();
                var response = await StartRequestForTest(new Uri($"https://localhost:{port}"));

                // Assert
                var test = mock.Received(1).Provide(Arg.Any<IStreamReader>());
            }
        }

        [Test]
        public async Task Should_Get_Right_Request_And_Response()
        {
            // Arrange
            var response = HttpResponse.CreateWithMessage(HttpResponseCode.Ok, "Hello World", false);
            using (var target = GetTarget(new HttpRequestProvider(), out int port))
            {
                Uri requestUri = new Uri($"https://localhost:{port}/foobar");
                IHttpRequest request = null;
                target.Use((context, next) =>
                {
                    request = context.Request;
                    context.Response = response;
                    return Task.CompletedTask;
                });

                // Act
                target.Start();
                var webresponse = await StartRequestForTest(requestUri);
                Encoding bodyEncoding = Encoding.GetEncoding(webresponse.CharacterSet);
                byte[] responseBodyBytes = await GetAllBytesFromStreamAsync(webresponse.GetResponseStream());
                byte[] expectedResponseBodyBytes = await GetEncodedBodyBytesForResponse(response, bodyEncoding);

                // Assert
                request.ShouldNotBe(null);
                request.Method.ShouldBe(HttpMethods.Get);
                request.Uri.ShouldBe(new Uri(requestUri.AbsolutePath, UriKind.Relative));
                request.ProtocolVersion.ShouldBe(HttpVersion.Version11);
                webresponse.ContentType.ShouldStartWith("text/html");
                webresponse.CharacterSet.ShouldBe("utf-8");
                CollectionAssert.AreEqual(expectedResponseBodyBytes, responseBodyBytes);
            }
        }

        [Test]
        public async Task Should_Get_Right_Request_From_Browser()
        {
            // Arrange
            var response = HttpResponse.CreateWithMessage(HttpResponseCode.Ok, "Hello World", false);
            using (var target = GetTarget(new HttpRequestProvider(), out int port))
            {
                Uri requestUri = new Uri($"https://localhost:{port}/foobar");
                TaskCompletionSource<IHttpRequest> ctsRequestReceived = new TaskCompletionSource<IHttpRequest>();
                target.Use((context, next) =>
                {
                    ctsRequestReceived.SetResult(context.Request);
                    context.Response = response;
                    return Task.CompletedTask;
                });

                // Act
                target.Start();
                IHttpRequest request;
                using (Process.Start(requestUri.ToString()))
                    request = await ctsRequestReceived.Task;

                // Assert
                request.ShouldNotBe(null);
                request.Method.ShouldBe(HttpMethods.Get);
                request.Uri.ShouldBe(new Uri(requestUri.AbsolutePath, UriKind.Relative));
                request.ProtocolVersion.ShouldBe(HttpVersion.Version11);
            }

        }

        /*private enum TaskWaitBehavior
        {
            Synchronous = 1,
            Asynchronous
        }

        private enum CausalityRelation
        {
            AssignDelegate,
            Join,
            Choice,
            Cancel,
            Error
        }
        private enum AsyncCausalityStatus
        {
            Canceled = 2,
            Completed = 1,
            Error = 3,
            Started = 0
        }
        private enum CausalitySynchronousWork
        {
            CompletionNotification,
            ProgressNotification,
            Execution
        }*/

        /**
         * TPL trace events:
         * TaskScheduled (int OriginatingTaskSchedulerID, int OriginatingTaskID, int TaskID, int CreatingTaskID, int TaskCreationOptions)
         * TaskStarted(int OriginatingTaskSchedulerID, int OriginatingTaskID, int TaskID)
         * TaskCompleted(int OriginatingTaskSchedulerID, int OriginatingTaskID, int TaskID, bool IsExceptional)
         * TaskWaitBegin(int OriginatingTaskSchedulerID, int OriginatingTaskID, int TaskID, TaskWaitBehavior Behavior, int ContinueWithTaskID)
         * TaskWaitEnd(int OriginatingTaskSchedulerID, int OriginatingTaskID, int TaskID)
         * TaskWaitContinuationComplete(int TaskID)
         * TaskWaitContinuationStarted(int TaskID)
         * AwaitTaskContinuationScheduled(int OriginatingTaskSchedulerID, int OriginatingTaskID, int ContinuwWithTaskId)
         * TraceOperationBegin(int TaskID, string OperationName, long RelatedContext)
         * TraceOperationRelation(int TaskID, CausalityRelation Relation)
         * TraceOperationEnd(int TaskID, AsyncCausalityStatus Status)
         * TraceSynchronousWorkBegin(int TaskID, CausalitySynchronousWork Work)
         * TraceSynchronousWorkEnd(CausalitySynchronousWork Work)
         * RunningContinuation(int TaskID, long Object)
         * RunningContinuationList(int TaskID, int Index, long Object)
         * DebugMessage(string Message)
         * DebugFacilityMessage(string Facility, string Message)
         * DebugFacilityMessage1(string Facility, string Message, string Value1)
         * SetActivityId(Guid NewId)
         * NewID(int TaskID)
         * */

        //class name System.Threading.Tasks.TplEtwProvider
        const string EventProviderName = "System.Threading.Tasks.TplEventSource";
        static readonly Guid EventProviderGuid = new Guid("2e5dba47-a3d2-4d16-8ee0-6671ffdcd7b5");

        [Test]
        public async Task Should_Not_Have_UnobservedTaskException()
        {
            // Arrange
            var workingDir = TestContext.CurrentContext.WorkDirectory;
            var tracingThread = new Thread(() =>
            {
                //see: https://stackoverflow.com/questions/28540728/how-do-i-listen-to-tpl-taskstarted-taskcompleted-etw-events
                string PrettyPrintEventPayload(TraceEvent @event)
                {
                    StringBuilder builder = new StringBuilder();
                    foreach (var name in @event.PayloadNames)
                    {
                        if (builder.Length != 0)
                            builder.Append(", ");
                        builder.AppendFormat("{0}={1}", name, @event.PayloadByName(name));
                    }
                    return builder.ToString();
                }
                using (var session = new TraceEventSession("TplCaptureSession"))
                using (var logOutput = File.CreateText(Path.Combine(workingDir, "TplEtwTrace.log")))
                {
                    session.EnableProvider(EventProviderGuid, TraceEventLevel.Always);
                    session.Source.Dynamic.AddCallbackForProviderEvent(EventProviderName,
                        null, @event =>
                        {
                            logOutput.WriteLine($"[{DateTime.Now.ToString("o")}] {@event.EventName}: {PrettyPrintEventPayload(@event)}");
                        });

                    session.Source.Process();
                }
            });
            tracingThread.IsBackground = true;
            try
            {
                tracingThread.Start();

                TaskCompletionSource<bool> tcsUnobservedCalled = new TaskCompletionSource<bool>();
                TaskScheduler.UnobservedTaskException += (sender, e) =>
                {
                    int taskId = ((Task)sender).Id;
                    Console.WriteLine($"The Task with Id {taskId} failed and following exception was not observed:");
                    Console.WriteLine(e.Exception.ToString());
                    tcsUnobservedCalled.TrySetResult(true);
                };

                // Act
                var task = Should_Get_Right_Request_From_Browser();
                await task;
                task = null;
                await Task.Yield();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();

                bool unobservedCalled = false;
                try
                {
                    unobservedCalled = Should.CompleteIn(tcsUnobservedCalled.Task, TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    //when it does not complete in 10 sec and there are no other problems with Tasks and GC, it's maybe a success
                    //Assert.Inconclusive();
                }

                // Assert
                unobservedCalled.ShouldBe(false);
            }
            finally
            {
                tracingThread.Abort();
            }
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_Succeed_Upload_File(bool sendChunked)
        {
            // Arrange
            var filePath = Assembly.GetExecutingAssembly().Location;
            using (var target = GetTarget(new HttpRequestProvider(), out int port))
            {
                Uri requestUri = new Uri($"https://localhost:{port}/upload");
                IHttpRequest request = null;
                target.Use(async (context, next) =>
                {
                    request = context.Request;
                    byte[] fileContents = null;
                    string contentType = null;
                    if ((await request.Post.ParsedAsync).TryGetMultipartItemByName("file", out var fileItem))
                    {
                        fileContents = fileItem.Body;
                        fileItem.Parsed.TryGetByName("content-type", out contentType);
                    }
                    context.Response = new HttpResponse(contentType, new MemoryStream(fileContents), request.KeepAliveConnection());
                    await next();
                });

                // Act
                target.Start();
                byte[] expectedResponseBodyBytes;
                byte[] responseBodyBytes;
                HttpWebResponse webresponse = null;
                try
                {
                    using (FileStream testFile = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
                    {
                        webresponse = await StartUploadFileForTest(requestUri, testFile, sendChunked);
                        testFile.Position = 0;
                        expectedResponseBodyBytes = await GetAllBytesFromStreamAsync(testFile);
                    }
                    responseBodyBytes = await GetAllBytesFromStreamAsync(webresponse.GetResponseStream());

                    // Assert
                    request.ShouldNotBe(null);
                    request.Method.ShouldBe(HttpMethods.Post);
                    request.Uri.ShouldBe(new Uri(requestUri.AbsolutePath, UriKind.Relative));
                    request.ProtocolVersion.ShouldBe(HttpVersion.Version11);
                    webresponse.ContentType.ShouldStartWith("application/octet-stream");
                    webresponse.CharacterSet.ShouldBeNullOrEmpty();
                }
                finally
                {
                    webresponse?.Dispose();
                }
                CollectionAssert.AreEqual(expectedResponseBodyBytes, responseBodyBytes);
            }
        }
    }
}
