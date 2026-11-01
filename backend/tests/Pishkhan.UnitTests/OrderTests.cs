using Pishkhan.Modules.Catalog.Services;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Orders.Domain;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.UnitTests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static Order Draft(params OrderItem[] items)
    {
        var order = Order.Create(Guid.NewGuid(), 7, OrderSources.Panel, null, null, Now);
        order.Edit(items, 500_000, 100_000, "مریم", null, null, null, Now);
        return order;
    }

    [Fact]
    public void Totals_are_lines_plus_shipping_minus_discount()
    {
        var order = Draft(new OrderItem(Guid.NewGuid(), Guid.NewGuid(), "مانتو", "سایز: M", null, 2_500_000, 2), new OrderItem(null, null, "بسته‌بندی کادو", null, null, 300_000, 1));

        order.Subtotal.ShouldBe(5_300_000);
        order.Total.ShouldBe(5_700_000);
        order.TotalMoney.ShouldBe(Money.Rial(5_700_000));
    }

    [Fact]
    public void A_discount_never_makes_the_total_negative()
    {
        var order = Order.Create(Guid.NewGuid(), 1, OrderSources.Panel, null, null, Now);
        order.Edit([new OrderItem(null, null, "x", null, null, 1_000, 1)], 0, 50_000, null, null, null, null, Now);
        order.Total.ShouldBe(0);
        order.MissingForPayment().ShouldContain("total");
    }

    [Fact]
    public void The_happy_path_runs_from_draft_to_completed()
    {
        var order = Draft(new OrderItem(null, null, "x", null, null, 1_000, 1));
        foreach (var status in new[] { OrderStatuses.AwaitingPayment, OrderStatuses.Paid, OrderStatuses.Preparing, OrderStatuses.Shipped, OrderStatuses.Delivered, OrderStatuses.Completed })
        {
            order.MoveTo(status, Now).ShouldBeTrue(status);
        }

        order.PaidAt.ShouldBe(Now);
        order.ClosedAt.ShouldBe(Now);
        order.Revision.ShouldBe(7);
    }

    [Theory]
    [InlineData(OrderStatuses.Draft, OrderStatuses.Paid)]
    [InlineData(OrderStatuses.Draft, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Paid, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Shipped, OrderStatuses.Refunded)]
    [InlineData(OrderStatuses.Cancelled, OrderStatuses.Draft)]
    [InlineData(OrderStatuses.Refunded, OrderStatuses.Paid)]
    public void Moves_outside_the_lifecycle_are_refused(string from, string to) => OrderStatuses.CanMove(from, to).ShouldBeFalse();

    [Fact]
    public void Every_status_has_its_moves_listed() => OrderStatuses.All.ShouldAllBe(s => OrderStatuses.Next.ContainsKey(s));

    [Fact]
    public void Only_a_draft_can_be_edited()
    {
        var order = Draft(new OrderItem(null, null, "x", null, null, 1_000, 1));
        order.MoveTo(OrderStatuses.AwaitingPayment, Now).ShouldBeTrue();

        Should.Throw<DomainException>(() => order.Edit([], 0, 0, null, null, null, null, Now));
        order.MoveTo(OrderStatuses.Draft, Now).ShouldBeTrue();
        order.Edit([], 0, 0, null, null, null, null, Now);
    }

    [Fact]
    public void Shipping_needs_address_city_postal_code_and_phone()
    {
        var order = Draft(new OrderItem(null, null, "x", null, null, 1_000, 1));
        order.MissingForShipping().ShouldBe(["address", "phone"]);

        order.Edit(order.Items, 0, 0, null, ("enc", "bidx"), new OrderAddress(null, "fars", "فارس", null, "خیابان زند", null), null, Now);
        order.MissingForShipping().ShouldBe(["city", "postalCode"]);
    }

    [Theory]
    [InlineData(null, OrderStatuses.Draft, 0)]
    [InlineData(OrderStatuses.AwaitingPayment, OrderStatuses.Paid, -1)]
    [InlineData(OrderStatuses.Paid, OrderStatuses.Preparing, 0)]
    [InlineData(OrderStatuses.Shipped, OrderStatuses.Delivered, 0)]
    [InlineData(OrderStatuses.Paid, OrderStatuses.Refunded, 1)]
    [InlineData(OrderStatuses.Delivered, OrderStatuses.Returned, 1)]
    [InlineData(OrderStatuses.Returned, OrderStatuses.Refunded, 0)]
    [InlineData(OrderStatuses.AwaitingPayment, OrderStatuses.Cancelled, 0)]
    public void Stock_moves_only_when_entering_or_leaving_paid_statuses(string? from, string to, int direction) =>
        OrderStockHandler.Direction(from, to).ShouldBe(direction);

    [Fact]
    public void Viewers_read_orders_and_agents_also_change_them()
    {
        WorkspaceRoles.PermissionsOf(WorkspaceRoles.Viewer).ShouldContain(Permissions.OrdersRead);
        WorkspaceRoles.PermissionsOf(WorkspaceRoles.Viewer).ShouldNotContain(Permissions.OrdersWrite);
        WorkspaceRoles.PermissionsOf(WorkspaceRoles.Agent).ShouldContain(Permissions.OrdersWrite);
        WorkspaceRoles.PermissionsOf(WorkspaceRoles.Admin).ShouldContain(Permissions.OrdersWrite);
    }
}
