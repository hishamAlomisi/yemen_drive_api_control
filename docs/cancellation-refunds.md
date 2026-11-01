# Cancellation and paid-ride refunds

`RideCancellationRequestModel/get` with `rideId` and `previewOnly: true` returns an ownership-checked refund quote. The rider confirms a centered breakdown before sending a paid cancellation request. Payment-related cancellation reasons apply only to unpaid assigned/ongoing rides.

The quote retains the snapshotted service and cancellation fees. For started rides it also retains completed travel, waiting and route-change fees. Traveled fare follows the original negotiated pricing factor and trusted driver GPS; it excludes recorded waiting time. The request freezes the computed refund and cancellation location. Missing, stale or discontinuous tracking requires an explicit administrative reviewed fare; the cancellation request itself remains available.

Customer request -> driver decision -> administrative approval is the paid workflow. Requesting/accepting cancellation creates no refund. Only final administrative approval credits the internal customer wallet and posts a separate immutable refund and balanced reversal journal. Floosak/Jaib refunds are credited to the application wallet. Available driver balance is debited for unearned entitlement; a shortfall is recorded as an independent driver debt and does not block the customer refund. Repeated approval has no duplicate financial effect.

Prepared confirmation/code workflows can be abandoned without another provider call, including when a preparation transaction ID exists. Dispatched/uncertain debits stay reserved while cancellation review is accepted. Final administrative settlement requires their outcome to be known. A late provider-confirmed debit can be recorded against a CancellationPending ride, then refunded through the existing cancellation review. New debit attempts remain blocked during review.

Unpaid rider cancellation before driver arrival is immediate; after arrival it follows driver review. An unpaid driver cancellation before departure is immediate. After departure or payment, a driver request requires passenger consent; refusal goes directly to administration. Required decisions keep the ride reserved until completion.

Cash-paid cancellation obtains administrative approval before the driver selects cash return or the passenger wallet (default). Wallet return requires passenger consent. Refusal repeats driver approval and refund selection without posting money. Cash return requires the driver's explicit acknowledgment that the net amount has been returned. Replayed or stale decisions cannot duplicate refunds.

Driver arrival is separate from starting the ride. Foreground passengers receive a full-screen arrival/voice alert across application pages and acknowledge it to the driver. Background passengers receive a persisted notification with sound; the driver sees their foreground availability. Stopping an alert survives reconnect, and stale alert IDs cannot stop a newer ring.

`ArrivalAndCashRefundConsent` adds arrival timestamps, alert and consent state. `ExternalWalletTopUps` allows the same withdrawal operation to represent a wallet top-up without a ride. Existing amounts/location fields hold the frozen cancellation quote. Startup repairs the known old cash driver-share mismatch with an independent balanced `CashCollectionShareCorrection` journal; source journals and operational balances are preserved.

Financial integration tests use an isolated temporary database; wallet tests use mocked provider HTTP with the installed provider libraries. No real payment or refund is used for verification.
