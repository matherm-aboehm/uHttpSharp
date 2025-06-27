using NUnit.Framework;
using Shouldly;
using System;
using System.Net;

namespace uhttpsharp.Tests
{
    public class HttpProtocolVersionProviderTests
    {
        private static IHttpProtocolVersionProvider GetTarget()
        {
            return new HttpProtocolVersionProvider();
        }

        private static Version GetExpectedProtocolVersion(string protocol)
        {
            switch (protocol)
            {
                case "HTTP/1.0": return HttpVersion.Version10;
                case "http/1.1": return HttpVersion.Version11;
                case "": return new Version(0, 9);
                case "HTTP/2": return new Version(2, 0);
                default: return null;
            }
        }

        [Test]
        [TestCase("HTTP/1.0")]
        [TestCase("http/1.1")]
        [TestCase("")]
        [TestCase("HTTP/2")]
        public void Should_Succeed_And_Get_Right_Version(string protocol)
        {
            // Arrange
            var target = GetTarget();
            var expected = GetExpectedProtocolVersion(protocol);

            // Act
            var actual = target.Provide(protocol);

            // Assert
            actual.ShouldBe(expected);
        }

        [Test]
        [ExpectedException(ExpectedException = typeof(FormatException))]
        public void Should_Fail()
        {
            // Arrange
            var target = GetTarget();

            // Act
            var actual = target.Provide("some random string");
        }
    }
}
