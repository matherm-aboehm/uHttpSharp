using NSubstitute;
using NUnit.Framework;
using Shouldly;
using System;
using System.Net;

namespace uhttpsharp.Tests
{
    public class HttpProtocolVersionProviderCacheTests
    {
        private const string ProtocolString = "Hello World";

        private static IHttpProtocolVersionProvider GetTarget(IHttpProtocolVersionProvider child)
        {
            return new HttpProtocolVersionProviderCache(child);
        }

        [Test]
        public void Should_Call_Child_With_Right_Parameters()
        {
            // Arrange
            var mock = Substitute.For<IHttpProtocolVersionProvider>();
            var target = GetTarget(mock);

            // Act
            target.Provide(ProtocolString);

            // Assert
            mock.Received(1).Provide(ProtocolString);
        }

        [Test]
        public void Should_Return_Same_Child_Value()
        {
            // Arrange
            Version expectedVersion = HttpVersion.Version11;

            var mock = Substitute.For<IHttpProtocolVersionProvider>();
            mock.Provide(ProtocolString).Returns(expectedVersion);
            var target = GetTarget(mock);


            // Act
            var actual = target.Provide(ProtocolString);

            // Assert
            actual.ShouldBe(expectedVersion);
        }

        [Test]
        public void Should_Cache_The_Value()
        {
            // Arrange
            var mock = Substitute.For<IHttpProtocolVersionProvider>();
            var target = GetTarget(mock);

            // Act
            target.Provide(ProtocolString);
            target.Provide(ProtocolString);
            target.Provide(ProtocolString);

            // Assert
            mock.Received(1).Provide(ProtocolString);
        }

    }
}
