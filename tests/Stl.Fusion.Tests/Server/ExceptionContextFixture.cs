﻿#if NETFRAMEWORK
using System.Web.Http.Filters;
using System.Web.Http.Controllers;
using System.Web.Http;
using System.Web.Http.Dependencies;
#else
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
#endif

using Moq;
using AutoFixture;
using Stl.Fusion.Server;

namespace Stl.Fusion.Tests.Server;

public class ExceptionContextFixture : BaseFixture<
#if NETFRAMEWORK
    HttpActionExecutedContext
#else
    ExceptionContext
#endif
>
{
    private readonly Mock<ILogger<JsonifyErrorsAttribute>> _logMock = new();
    private readonly Mock<IErrorRewriter> _errorRewriterMock = new();
#if NETFRAMEWORK
    private readonly Mock<IDependencyResolver> _serviceProviderMock = new();
#else
    private Mock<IServiceProvider> _serviceProviderMock = new();
    private Mock<IActionResultExecutor<ContentResult>> _actionResultExecutorMock = new();
#endif

    public ExceptionContextFixture()
    {
        _errorRewriterMock.Setup(x =>
                x.Rewrite(It.IsAny<object>(), It.IsAny<Exception>(), It.IsAny<bool>()))
            .Returns((object _, Exception x, bool _) => x);

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(IErrorRewriter)))
            .Returns(_errorRewriterMock.Object);

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ILogger<JsonifyErrorsAttribute>)))
            .Returns(_logMock.Object);

        Fixture.Register(() => _serviceProviderMock);

#if NETFRAMEWORK
        Fixture.Customize<HttpActionExecutedContext>(x => 
            x.Without(x => x.Response));
        Fixture.Register(() =>
            new HttpActionContext {
                ControllerContext = new HttpControllerContext {
                    Request = new HttpRequestMessage(),
                    Configuration = Fixture.Create<HttpConfiguration>(),
                },
            });
#else
        Fixture.Customize<BindingInfo>(x => x.OmitAutoProperties());

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(IActionResultExecutor<ContentResult>)))
            .Returns(_actionResultExecutorMock.Object);
#endif
    }

    public ExceptionContextFixture WithException(string message)
    {
        Fixture.Register(() => new Exception(message));
        return this;
    }

    public void VerifyLogError(string message)
    {
        _logMock.VerifyLog(log => log.LogError(
            It.IsAny<Exception>(), "Error message: {Message}", message));
    }

    public void VerifyErrorRewrite(bool isExpected)
    {
        _errorRewriterMock.Verify(x => x.Rewrite(It.IsAny<object>(), It.IsAny<Exception>(), It.IsAny<bool>()),
            Times.Exactly(isExpected ? 1 : 0));
    }
}