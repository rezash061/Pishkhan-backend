# 0013. Card-to-card receipt verification

- Status: accepted
- Date: 2026-11-04
- Work package: BE-2.10 (gap G1), backend plan 9.10 and 10.5

## Context

Many Iranian shops take payment by card-to-card transfer, and the customer sends a screenshot of the bank app's receipt. The seller
checks it by eye, and fake or re-sent receipts are a known fraud. The plan asks for extraction with a vision model, checks against the
order and the seller's accounts, duplicate detection, and auto-approve only once extraction reaches 95% precision on a labeled set.

## Decision

- Receipts live in the Payments module. A receipt comes from an image a customer sends while they have an order awaiting payment (in a
  workspace with a registered bank account), or from a member in the panel (an uploaded file or a customer's image). Every image goes
  through the Files module and is used only once it is scanned clean.
- Extraction is one call to the `extract_receipt` route with prompt `receipt.extract`, answering a fixed json shape. Persian digits
  and Toman amounts are normalised in code, not trusted to the model. A model outage leaves the receipt in review, never decided.
- The checks are a pure function (`ReceiptRules.Evaluate`): order, amount (with a seller tolerance), destination card or Sheba
  (masked digits compared position by position), time window, reference format, duplicates. Duplicates are the same reference number
  or a 64-bit difference hash of the image (computed with SkiaSharp, already a dependency) within 6 bits of an earlier receipt's.
- Only exact duplicates are rejected automatically, and the seller can still approve one. Auto-approve needs the platform switch
  `Payments:ReceiptAutoApprove` (off until the labeled set shows 95% precision), the seller's setting, confidence over their threshold
  and every check passing. Everything else waits for the seller.
- Approval publishes the existing `PaymentConfirmed` event with provider `card_to_card`, so the Orders module moves the order to `paid`
  exactly as for gateway payments.

## Consequences

- No new package. Bank receipt templates (layout hints per bank app, managed in Backoffice) and tamper heuristics are left for later;
  the generic reference-format check stands in for them.
- Receipts are metered on `receipt_verifications`; past the plan's limit they are not read by the model and go to the seller.
- Bale images are not taken yet (Bale media download is still to come).
