namespace Auction.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Common;
using Auction.Api.Controllers;
using Auction.Application.Common.Models;
using Auction.Application.Services.Auction;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

/// <summary>
/// The two routes that move an auction's clock exist for testing only. They answer 404 unless AdminTimeControls:Enabled is on and the host is
/// not Production; when open, only an admin may use them (staff and every other role get 403).
/// </summary>
public class AdminTimeControlsTests
{
    private static IConfiguration Config(string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            enabled is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [AdminTimeControlGate.ConfigKey] = enabled }).Build();

    private static IHostEnvironment Env(string name)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(item => item.EnvironmentName).Returns(name);
        return env.Object;
    }

    // ------------------------------------------------------------------ the gate

    [Theory]
    [InlineData("Development", "true", true)]
    [InlineData("Staging", "true", true)]
    [InlineData("QA", "True", true)]
    [InlineData("Production", "true", false)]      // a Production host never opens them, whatever the setting says
    [InlineData("production", "true", false)]
    [InlineData("Development", "false", false)]
    [InlineData("Development", null, false)]       // off by default: an unset flag is closed
    [InlineData("Development", "", false)]
    [InlineData("Development", "yes please", false)]
    [InlineData("Production", "false", false)]
    [InlineData("Production", null, false)]
    public void The_gate_is_open_only_when_the_flag_is_on_and_the_host_is_not_production(string environment, string? flag, bool open)
    {
        AdminTimeControlGate.IsOpen(Config(flag), Env(environment)).Should().Be(open);
    }

    // ------------------------------------------------------------------ the filter (runs before binding and authentication)

    private static ResourceExecutingContext FilterContext(string? flag, string environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Config(flag));
        services.AddSingleton(Env(environment));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ResourceExecutingContext(action, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    [Theory]
    [InlineData(null, "Development")]
    [InlineData("false", "Development")]
    [InlineData("true", "Production")]
    public void While_the_gate_is_closed_the_filter_answers_404_not_found(string? flag, string environment)
    {
        var context = FilterContext(flag, environment);

        var act = () => new RequireAdminTimeControlsAttribute().OnResourceExecuting(context);

        var error = act.Should().Throw<DomainException>().Which;
        error.StatusCode.Should().Be(404);
        error.ErrorCode.Should().Be("NOT_FOUND");
        error.Message.Should().NotContain("admin").And.NotContain("flag").And.NotContain("AdminTimeControls");    // reveals nothing about why
    }

    [Theory]
    [InlineData("true", "Development")]
    [InlineData("true", "Staging")]
    public void When_the_gate_is_open_the_filter_lets_the_request_through(string flag, string environment)
    {
        var context = FilterContext(flag, environment);

        new RequireAdminTimeControlsAttribute().OnResourceExecuting(context);

        context.Result.Should().BeNull();
    }

    [Fact]
    public void Both_routes_carry_the_filter()
    {
        foreach (var name in new[] { nameof(AuctionsController.AdminUpdateSchedule), nameof(AuctionsController.AdminUpdateSettlementDeadlines) })
        {
            var method = typeof(AuctionsController).GetMethod(name)!;
            method.GetCustomAttributes(typeof(RequireAdminTimeControlsAttribute), inherit: false).Should().HaveCount(1, name);
        }
    }

    // ------------------------------------------------------------------ the role check, once the gate is open

    private static (AuctionsController Controller, Mock<IAuctionService> Service) Controller(string? roles, bool withUser = true)
    {
        var service = new Mock<IAuctionService>();
        service.Setup(item => item.AdminUpdateScheduleAsync(It.IsAny<ActorContext>(), It.IsAny<Guid>(), It.IsAny<AdminUpdateAuctionScheduleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminAuctionScheduleUpdatedResponse(Guid.NewGuid(), AuctionStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        service.Setup(item => item.AdminUpdateSettlementDeadlinesAsync(It.IsAny<ActorContext>(), It.IsAny<Guid>(), It.IsAny<AdminUpdateSettlementDeadlinesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminSettlementDeadlinesUpdatedResponse(Guid.NewGuid(), null, null, null, null, null, DateTimeOffset.UtcNow));
        var http = new DefaultHttpContext();
        if (withUser) http.Request.Headers["x-user-id"] = Guid.NewGuid().ToString();
        if (roles is not null) http.Request.Headers["x-user-roles"] = roles;
        var controller = new AuctionsController(service.Object) { ControllerContext = new ControllerContext { HttpContext = http } };
        return (controller, service);
    }

    private static Task<IActionResult> Schedule(AuctionsController c) =>
        c.AdminUpdateSchedule(Guid.NewGuid(), new AdminUpdateAuctionScheduleRequest(RemainingSeconds: 0), CancellationToken.None);

    private static Task<IActionResult> Deadlines(AuctionsController c) =>
        c.AdminUpdateSettlementDeadlines(Guid.NewGuid(), new AdminUpdateSettlementDeadlinesRequest(PaymentRemainingSeconds: 0), CancellationToken.None);

    public static IEnumerable<object[]> Routes()
    {
        yield return new object[] { "schedule", (Func<AuctionsController, Task<IActionResult>>)Schedule };
        yield return new object[] { "settlement-deadlines", (Func<AuctionsController, Task<IActionResult>>)Deadlines };
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_admin_may_use_the_route(string route, Func<AuctionsController, Task<IActionResult>> call)
    {
        var (controller, service) = Controller("admin");

        var result = await call(controller);

        result.Should().BeOfType<OkObjectResult>($"{route} is open to an admin");
        service.Invocations.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Staff_get_403_and_the_service_is_never_called(string route, Func<AuctionsController, Task<IActionResult>> call)
    {
        var (controller, service) = Controller("staff");

        var result = await call(controller);

        var forbidden = result.Should().BeOfType<ObjectResult>(route).Subject;
        forbidden.StatusCode.Should().Be(403);
        forbidden.Value!.ToString().Should().Contain("ROLE_NOT_ALLOWED");
        service.Invocations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_other_role_gets_403_too(string route, Func<AuctionsController, Task<IActionResult>> call)
    {
        foreach (var roles in new[] { "buyer", "seller", "buyer,seller", "moderator", "" })
        {
            var (controller, service) = Controller(roles);

            var result = await call(controller);

            result.Should().BeOfType<ObjectResult>($"{route} / '{roles}'").Which.StatusCode.Should().Be(403);
            service.Invocations.Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_admin_who_also_holds_another_role_still_passes_and_staff_plus_buyer_still_fails(string route, Func<AuctionsController, Task<IActionResult>> call)
    {
        (await call(Controller("staff,admin").Controller)).Should().BeOfType<OkObjectResult>(route);
        (await call(Controller("staff,buyer").Controller)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_request_without_an_authenticated_user_is_refused_before_any_role_is_considered(string route, Func<AuctionsController, Task<IActionResult>> call)
    {
        var (controller, service) = Controller("admin", withUser: false);

        var result = await call(controller);

        result.Should().BeOfType<ObjectResult>(route).Which.StatusCode.Should().BeOneOf(400, 401);
        service.Invocations.Should().BeEmpty();
    }
}
