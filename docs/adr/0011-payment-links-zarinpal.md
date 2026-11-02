# 0011. Payment links: the seller's own Zarinpal account, a fixed amount, verify before paying

Status: accepted (2026-11-02)

Payments are their own module (`payments` schema). Each workspace connects its own Zarinpal merchant account, so the money goes straight from the customer to the seller and Pishkhan never holds or settles it. The merchant ID is encrypted at rest with `ISecretProtector` (purpose `payments.merchant`) and only its last four characters are ever returned. Providers sit behind `IPaymentGateway`, so another PSP can be added without touching the flow.

A payment link is made for one order and fixes the amount, the shop name and the lines when it is made. The customer only sees the link's random 32-character token, never an order or workspace id. A new link for the same order cancels the older ones, and an order leaving `awaiting_payment` cancels its active links (Payments consumes `orders.order-status-changed.v1`, commerce group), so an old amount cannot be paid after the order changed.

The amount sent to Zarinpal and the amount verified are always the link's own. The callback only identifies the attempt (the authority) and says whether the customer finished. Its `Status=OK` is never trusted: we pay only when Zarinpal's verify call answers 100 or 101 for our amount. A repeated or late callback finds the attempt already completed and verifies nothing again. Two callbacks at once are settled by the link's row version.

A verified payment is money the customer has paid, so it is recorded even if the link was cancelled or expired meanwhile. Payments then publishes `payments.payment-confirmed.v1`, and Orders moves the order to `paid` only if it is still awaiting payment for the same total. Otherwise Orders adds a note to the order for the seller to resolve (refund or accept), rather than guessing.

The payment page is a small server-rendered Persian page on the Api host (`/pay`, proxied from the payment domain), with no scripts and a CSP whose `form-action` allows only us and Zarinpal. That keeps the customer's side independent of the panel and the website, works on any phone browser that Instagram opens, and gives the callback a first-party URL. Requests to Zarinpal are not retried automatically, because a retried request would open a second payment; the customer presses the button again instead, and an unreachable Zarinpal during verify leaves the attempt open.
