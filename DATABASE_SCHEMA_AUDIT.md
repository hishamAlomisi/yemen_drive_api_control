# جرد مخطط قاعدة البيانات — المرحلة 3.1

تاريخ الجرد: 2026-09-12  
النطاق: API، لوحة الإدارة، تطبيق العميل، وتطبيق السائق.  
قاعدة السلامة: هذه الوثيقة جرد فقط؛ لا يعني تصنيف أي جدول بأنه «مؤجل القرار» أنه قابل للحذف. لا يُحذف أي جدول أو عمود قبل ترحيل رسمي ومراجعة بياناته الفعلية.

## ملخص

- المخطط يحتوي على 24 جدول تشغيل.
- 17 جدولاً مرتبطة مباشرة بخدمة API أو بعقد مستخدم من إحدى الواجهات.
- 7 جداول لا تملك حتى الآن خدمة API أو شاشة مستهلكة مباشرة؛ تُحفظ لمراحل المالية المتقدمة أو السلامة أو تتبع المسار.
- تطبيق العميل: `E:\Eqs\yemen_drive`.
- تطبيق السائق: `E:\Eqs\yemen_drive_driver`.
- لوحة الإدارة: `src/YemenDrive.Api/wwwroot`.

## الجداول المستخدمة فعلياً

| الجدول | الحقول التشغيلية الأساسية | المستهلكون | الحالة |
|---|---|---|---|
| Users | PhoneNumber, DisplayName, Email, PasswordHash, Role, IsActive | المصادقة، العميل، السائق، الإدارة | مستخدم |
| DriverProfiles | UserId, ServiceKindId, ServiceCatalogItemId, VehicleModel, PlateNumber, IsAvailable | DriverModel، إدارة السائقين، اختيار عروض الرحلات | مستخدم |
| DriverLiveLocations | DriverId, Latitude, Longitude, Bearing, Speed, IsOnline, ObservedAtUtc | DriverLocationModel، لوحة الإدارة | مستخدم |
| SavedPlaces | UserId, Label, Kind, Address, Latitude, Longitude | تطبيق العميل والسائق، SavedPlaceModel | مستخدم |
| Rides | CustomerId, DriverId, Status, الخدمة، المواقع، الأسعار، RowVersion، التوقيت | العميل، السائق، الإدارة، RideModel | مستخدم |
| RideOffers | RideId, DriverId, Amount, Status, ExpiresAtUtc, Note | العميل، السائق، RideOfferModel | مستخدم |
| Wallets | UserId, Balance, Currency, RowVersion | WalletModel، الدفع النقدي | مستخدم |
| WalletTransactions | WalletId, RideId, Type, Amount, BalanceAfter, Description, ExternalReference | المحفظة والتحصيل النقدي | مستخدم |
| Notifications | UserId, Type, Title, Body, DataJson, IsRead | السائق، NotificationModel | مستخدم |
| PaymentTransactions | UserId, RideId, Amount, Currency, Provider, Status, ProviderReference, IdempotencyKey | PaymentModel، DriverCashPaymentModel | مستخدم |
| Promotions | Code, Name, مبالغ الخصم، الفترة، الاستخدام، IsActive | PromotionModel وتطبيقات الحساب | مستخدم |
| SupportTickets | UserId, Category, Message, Status, AdminReply, ResolvedAtUtc | العميل، السائق، SupportTicketModel | مستخدم |
| ReferralRedemptions | UserId, Code, Status | العميل، السائق، ReferralModel | مستخدم |
| PricingRules | ServiceKindId, ServiceCatalogItemId, BaseFare, PerKilometer, PerMinute, DriverShareRate, IsActive | PricingRuleModel ولوحة الإدارة | مستخدم |
| CommunicationMessages | RideId, SenderId, RecipientId, MessageType, Content, ReadAtUtc | السائق، RideMessageModel | مستخدم |
| RideServiceCatalogItems | Code, NameAr, ServiceKindId, الأسعار والوصف والحالة | العميل، السائق، الإدارة، ServiceCatalogModel | مستخدم |
| ServiceKinds | Code, NameAr, ImageUrl, IsActive, SortOrder | العميل، السائق، الإدارة، ServiceKindModel | مستخدم |

## جداول لها استخدام مستقبلي معتمد ولا تُحذف

| الجدول | الغرض المتوقع | نتيجة البحث الحالي | القرار |
|---|---|---|---|
| PaymentCardTokens | تخزين رموز بطاقات من مزود الدفع | لا توجد خدمة أو شاشة حالياً | الاحتفاظ؛ ضروري عند تفعيل دفع البطاقة |
| LedgerAccounts | دليل حسابات محاسبي | لا توجد خدمة أو شاشة حالياً | الاحتفاظ؛ أساس تسجيل النقد والعمولة والتسويات |
| JournalEntries | رؤوس قيود يومية | لا توجد خدمة أو شاشة حالياً | الاحتفاظ؛ يسجل العملية المالية كوحدة مراجعة |
| JournalLines | سطور قيود يومية وربطها بالمستخدم/الرحلة | لا توجد خدمة أو شاشة حالياً | الاحتفاظ؛ يوزع المبالغ بين العميل والسائق والمنصة |
| DriverSettlements | تسويات مستحقات السائق | لا توجد خدمة أو شاشة حالياً | الاحتفاظ؛ للزيادة أو النقص والعمولة وصافي مستحقات السائق |
| EmergencyRecordings | تسجيلات السلامة والطوارئ | لا توجد خدمة أو شاشة حالياً | الاحتفاظ؛ سيربط بإجراءات السلامة للرحلة |
| LocationUpdates | نقاط مسار الرحلة | لا توجد خدمة مباشرة حالياً | الاحتفاظ؛ لسجل مواقع العميل والسائق ومسار الرحلة |

## العلاقات الأساسية

- User ↔ DriverProfile: واحد إلى واحد، وUser ↔ Wallet: واحد إلى واحد.
- User → SavedPlaces وSupportTickets وReferralRedemptions: واحد إلى متعدد.
- Ride → Customer وDriver: علاقات مقيّدة الحذف، وRide → ServiceKind وRideServiceCatalogItem.
- Ride → RideOffers وLocationUpdates: واحد إلى متعدد.
- RideOffer → Driver، وDriverProfile → نوع الخدمة والخدمة المختارة.
- Wallet → WalletTransactions، وPaymentTransaction → User والرحلة اختيارياً.
- PricingRule → نوع الخدمة والخدمة المختارة.
- JournalEntry → JournalLines، وJournalLine → LedgerAccount.

## الأنواع والفهارس والقيود المعتمدة

- المبالغ المالية تستخدم `decimal(18,2)`.
- الهاتف فريد، وملف السائق ومحفظة المستخدم فريدان لكل مستخدم.
- العرض المعلق فريد لكل زوج رحلة/سائق.
- مرجع الدفع ومفتاح عدم التكرار فريدان عند وجودهما.
- دفعة نهائية واحدة فقط لكل رحلة.
- `Ride.RowVersion` و`Wallet.RowVersion` للحماية من التزامن.
- القيود تمنع الرصيد السالب، والحركات والمدفوعات غير الموجبة، وأسعار الرحلة السالبة.

## قرارات المرحلة التالية

1. لا يوجد حذف مخطط ضمن المرحلة 3.1.
2. عند تسجيل تحصيل نقدي أو زيادة أو نقص أو عمولة منصة، يجب إنشاء قيود محاسبية مرتبطة بالرحلة والعميل والسائق، ثم تستخدم `DriverSettlements` لتجميع مستحقات السائق للفترة.
3. عند تفعيل إجراءات الأمان للرحلة، يُربط `EmergencyRecordings` بالرحلة والمستخدم، إلى جانب مشاركة الرحلة وأرقام الطوارئ والتسجيل الصوتي. يحتفظ الجدول بالمرجع الآمن للملف وبصمته، لا بمحتواه داخل قاعدة البيانات.
4. عند تفعيل التتبع، تستخدم `LocationUpdates` كسجل تاريخي لنقاط مواقع العميل والسائق ومسار الرحلة، بينما يبقى `DriverLiveLocations` للموقع الحي الأخير على الخريطة.
5. لا يتم دمج أي من هذه الجداول أو حذفها قبل تنفيذ هذه الوظائف وكتابة migrations واختبارات لها.
6. المرحلة التالية: إغلاق شرط `recreateSchema` خارج بيئة التطوير، ثم مراجعة قيود حالات الرحلة والعروض واختبارات التزامن.
