# 0010. Orders: catalog prices, per-workspace numbers, stock on payment

Status: accepted (2026-11-01)

Orders are their own module (`orders` schema). A catalog line always takes its price from the catalog when it is added, through `ICatalogQueries`. The request can carry no price for it, and the AI's `create_order` tool has no price parameter at all. The line keeps that title and price afterwards, so a later catalog change does not change an order that was already placed. Custom lines with their own title and price exist only in the panel. Totals are subtotal plus shipping fee minus discount, never below zero.

Order numbers come from `orders.counters`, one row per workspace, through one atomic `INSERT ... ON CONFLICT DO UPDATE ... RETURNING`. A number taken by a request that then fails is skipped. Numbers are unique and increasing, but there can be gaps. That is simpler than a gap-free sequence and is enough for an order number.

Stock moves on payment, not when an order is created. A draft holds nothing, because the AI creates drafts freely and most of them are never paid. Entering `paid` takes the counted stock. Leaving the paid statuses for `cancelled`, `refunded` or `returned` puts it back. The Catalog module does this when it receives `orders.order-status-changed.v1` (commerce group, idempotent), so Orders never writes to catalog tables. Stock never goes below zero: when two orders are paid for the last unit, the seller resolves it. Creating an order or a draft checks the stock at that moment.

There is one AI draft per conversation. When `create_order` is called again in the same conversation, it replaces the lines of the conversation's AI draft. Once the seller moves that draft on, the next call starts a new draft. The test chat and the demo pass no message id. There the tool only prices the lines and saves nothing.

The customer's mobile number is encrypted and has a blind index (backend plan 3.4), the same treatment contact phone numbers get. The address is stored as province code, province, city, line and postal code. `AddressParser` fills these from free text. It only fills a field from a known province or city name, or from a valid postal code. The province list has the 31 provinces with their larger cities, and any other city name is accepted as typed.
