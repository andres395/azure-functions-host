// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Xunit;
using IApplicationLifetime = Microsoft.AspNetCore.Hosting.IApplicationLifetime;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class WorkerFunctionInvokerTests
    {
        private readonly TestWorkerFunctionInvoker _testFunctionInvoker;
        private readonly Mock<IApplicationLifetime> _applicationLifetime;
        private readonly Mock<IFunctionInvocationDispatcher> _mockFunctionInvocationDispatcher;
        private Mock<HttpRequest> _mockHttpRequest;

        public WorkerFunctionInvokerTests()
        {
            _mockHttpRequest = new Mock<HttpRequest>();
            _applicationLifetime = new Mock<IApplicationLifetime>();
            _mockFunctionInvocationDispatcher = new Mock<IFunctionInvocationDispatcher>();
            _mockFunctionInvocationDispatcher.Setup(a => a.ErrorEventsThreshold).Returns(0);

            var hostBuilder = new HostBuilder()
                .ConfigureDefaultTestWebScriptHost(o =>
                {
                    o.ScriptPath = TestHelpers.FunctionsTestDirectory;
                    o.LogPath = TestHelpers.GetHostLogFileDirectory().Parent.FullName;
                });
            var host = hostBuilder.Build();

            var sc = host.GetScriptHost();

            FunctionMetadata metaData = new FunctionMetadata();
            _testFunctionInvoker = new TestWorkerFunctionInvoker(sc, null, metaData, NullLoggerFactory.Instance, null, new Collection<FunctionBinding>(),
                _mockFunctionInvocationDispatcher.Object, _applicationLifetime.Object, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task InvokeTimeout_CallsShutdown()
        {
            try
            {
                _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.Initializing);
                await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), Task.Delay(TimeSpan.FromSeconds(30)));
            }
            catch (Exception)
            {
            }
            _applicationLifetime.Verify(a => a.StopApplication(), Times.Once);
        }

        [Theory]
        [InlineData(FunctionInvocationDispatcherState.Default, false)]
        [InlineData(FunctionInvocationDispatcherState.Initializing, true)]
        [InlineData(FunctionInvocationDispatcherState.Initialized, false)]
        [InlineData(FunctionInvocationDispatcherState.WorkerProcessRestarting, true)]
        [InlineData(FunctionInvocationDispatcherState.Disposing, true)]
        [InlineData(FunctionInvocationDispatcherState.Disposed, true)]
        public async Task FunctionDispatcher_DelaysInvoke_WhenNotReady(FunctionInvocationDispatcherState state, bool delaysExecution)
        {
            _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(state);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
            var invokeCoreTask = _testFunctionInvoker.InvokeCore(new object[] { }, null);
            var result = await Task.WhenAny(invokeCoreTask, timeoutTask);
            if (delaysExecution)
            {
                Assert.Equal(timeoutTask, result);
            }
            else
            {
                Assert.Equal(invokeCoreTask, result);
            }
        }

        [Fact]
        public async Task InvokeInitialized_DoesNotCallShutdown()
        {
            try
            {
                _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.Initialized);
                await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), Task.Delay(TimeSpan.FromSeconds(125)));
            }
            catch (Exception)
            {
            }
            _applicationLifetime.Verify(a => a.StopApplication(), Times.Never);
        }

        [Fact]
        public void HandleCancellationTokenParameter_NullParameters_ReturnsCancellationTokenNone()
        {
            var result = _testFunctionInvoker.HandleCancellationTokenParameter(null, null);
            Assert.Equal(CancellationToken.None, result);
        }

        [Fact]
        public void HandleCancellationTokenParameter_CancellationTokenParameter_ReturnsCancellationToken()
        {
            CancellationTokenSource cts = new();
            var cancellationTokenParameter = cts.Token;

            var result = _testFunctionInvoker.HandleCancellationTokenParameter(cancellationTokenParameter, null);

            Assert.Equal(cancellationTokenParameter, result);
            cts.Cancel();
            Assert.True(result.IsCancellationRequested);
        }

        [Fact]
        public void HandleCancellationTokenParameter_HttpCancellationToken_ReturnsCancellationToken()
        {
            CancellationTokenSource cts = new();
            var requestAborted = cts.Token;

            HttpContext httpContext = new DefaultHttpContext() { RequestAborted = requestAborted };
            _mockHttpRequest.Setup(m => m.HttpContext).Returns(httpContext);

            var result = _testFunctionInvoker.HandleCancellationTokenParameter(null, _mockHttpRequest.Object);

            Assert.Equal(requestAborted, result);
            cts.Cancel();
            Assert.True(result.IsCancellationRequested);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HandleCancellationTokenParameter_TwoCancellationTokens_ReturnsCancellationToken(bool cancelHostSource)
        {
            CancellationTokenSource hostCts = new();
            var cancellationTokenParameter = hostCts.Token;

            CancellationTokenSource httpCts = new();
            var requestAborted = httpCts.Token;

            HttpContext httpContext = new DefaultHttpContext() { RequestAborted = requestAborted };
            _mockHttpRequest.Setup(m => m.HttpContext).Returns(httpContext);

            var result = _testFunctionInvoker.HandleCancellationTokenParameter(cancellationTokenParameter, _mockHttpRequest.Object);

            Assert.NotEqual(cancellationTokenParameter, result);
            Assert.NotEqual(requestAborted, result);
            Assert.NotEqual(CancellationToken.None, result);

            if (cancelHostSource)
            {
                hostCts.Cancel();
            }
            else
            {
                httpCts.Cancel();
            }

            Assert.True(result.IsCancellationRequested);
        }
    }
}
