using NSubstitute;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
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
                byte[] exprectedResponseBodyBytes = await GetEncodedBodyBytesForResponse(response, bodyEncoding);

                // Assert
                request.ShouldNotBe(null);
                request.Method.ShouldBe(HttpMethods.Get);
                request.Uri.ShouldBe(new Uri(requestUri.AbsolutePath, UriKind.Relative));
                request.ProtocolVersion.ShouldBe(HttpVersion.Version11);
                webresponse.ContentType.ShouldStartWith("text/html");
                webresponse.CharacterSet.ShouldBe("utf-8");
                CollectionAssert.AreEqual(exprectedResponseBodyBytes, responseBodyBytes);
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
    }
}
