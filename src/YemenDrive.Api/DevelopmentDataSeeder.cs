using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Shared.Security;
using YemenDrive.Application.Places;
using SavedPlaceEntity = YemenDrive.Database.Entities.SavedPlace;

namespace YemenDrive.Api;

/// <summary>
/// Development-only, non-destructive sample data. Every item has a stable
/// DEV-SEED marker and is only inserted when it does not exist already.
/// </summary>
public sealed class DevelopmentDataSeeder(
    YemenDriveDbContext db,
    FinancialAccountProvisioningService financialAccounts)
{
    private const string Marker = "DEV-SEED";

    public async Task<object> SeedAsync(CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var transport = await EnsureKindAsync("DEV-SEED-TRANSPORT", "نقل تجريبي", 1, token);
        var delivery = await EnsureKindAsync("DEV-SEED-DELIVERY", "توصيل تجريبي", 2, token);
        await financialAccounts.ProvisionServiceKindAsync(transport, token);
        await financialAccounts.ProvisionServiceKindAsync(delivery, token);
        var economy = await EnsureServiceAsync(transport, "DEV-SEED-ECONOMY", "سيارة اقتصادية", 4, 1500, token);
        var premium = await EnsureServiceAsync(transport, "DEV-SEED-PREMIUM", "سيارة مميزة", 4, 2400, token);
        await EnsureServiceAsync(delivery, "DEV-SEED-DELIVERY-BIKE", "دراجة توصيل", 1, 900, token);

        var customer = await EnsureUserAsync("701000101", "عميل تجريبي", UserRole.Customer, token);
        var driverOne = await EnsureUserAsync("701000201", "سائق تجريبي 1", UserRole.Driver, token);
        var driverTwo = await EnsureUserAsync("701000202", "سائق تجريبي 2", UserRole.Driver, token);
        await financialAccounts.ProvisionUserAsync(customer, token);
        await financialAccounts.ProvisionUserAsync(driverOne, token);
        await financialAccounts.ProvisionUserAsync(driverTwo, token);
        await EnsureDriverAsync(driverOne, transport, economy, "سيارة تجريبية 1", "DEV-101", 4.8m, token);
        await EnsureDriverAsync(driverTwo, transport, premium, "سيارة تجريبية 2", "DEV-202", 4.7m, token);
        await EnsureLiveLocationAsync(driverOne, 15.3694, 44.1910, token);
        await EnsureLiveLocationAsync(driverTwo, 15.3558, 44.2044, token);
        await EnsureSavedPlacesAsync(customer, token);
        await EnsureServiceAreaAsync(token);
        await EnsurePricingAsync(transport, economy, 1200, 120, 20, token);
        await EnsurePricingAsync(transport, premium, 2000, 180, 25, token);

        var searching = await EnsureRideAsync(customer, transport, economy, "DEV-SEED-SEARCHING", RideStatus.Searching, null, token);
        var negotiating = await EnsureRideAsync(customer, transport, economy, "DEV-SEED-NEGOTIATING", RideStatus.Negotiating, null, token);
        await EnsureOfferAsync(negotiating, driverOne, 1700, OfferStatus.Pending, token);
        var active = await EnsureRideAsync(customer, transport, economy, "DEV-SEED-ACTIVE", RideStatus.DriverEnRoute, driverOne, token);
        await EnsureOfferAsync(active, driverOne, 1800, OfferStatus.Accepted, token);
        var completed = await EnsureRideAsync(customer, transport, premium, "DEV-SEED-COMPLETED", RideStatus.Completed, driverTwo, token);
        await EnsureOfferAsync(completed, driverTwo, 2600, OfferStatus.Accepted, token);
        await EnsureRideDataAsync(active, customer, driverOne, token);
        await EnsureNotificationsAsync(customer, driverOne, searching, active, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        return new
        {
            createdOrAvailable = true,
            message = "بيانات التطوير التجريبية جاهزة دون حذف أي بيانات قائمة.",
            serviceKinds = 2,
            serviceCatalogItems = 3,
            users = 3,
            drivers = 2,
            rides = 4
        };
    }

    private async Task<ServiceKind> EnsureKindAsync(string code, string name, int sortOrder, CancellationToken token)
    {
        var entity = await db.ServiceKinds.SingleOrDefaultAsync(x => x.Code == code, token);
        if (entity is not null) return entity;
        entity = new ServiceKind { Code = code, NameAr = name, IsActive = true, SortOrder = sortOrder };
        db.ServiceKinds.Add(entity);
        await db.SaveChangesAsync(token);
        return entity;
    }

    private async Task<RideServiceCatalogItem> EnsureServiceAsync(ServiceKind kind, string code, string name, int seats, decimal price, CancellationToken token)
    {
        var entity = await db.ServiceCatalogItems.SingleOrDefaultAsync(x => x.Code == code, token);
        if (entity is not null) return entity;
        entity = new RideServiceCatalogItem
        {
            Code = code, NameAr = name, ServiceKindId = kind.Id, ArrivalMinutes = 5,
            BasePrice = price, Rating = 4.7m, Seats = seats,
            DescriptionAr = $"{Marker}: بيانات خدمة للتطوير", IsRecommended = name.Contains("اقتصادية"), IsActive = true, SortOrder = 1
        };
        db.ServiceCatalogItems.Add(entity);
        await db.SaveChangesAsync(token);
        return entity;
    }

    private async Task<User> EnsureUserAsync(string phone, string name, UserRole role, CancellationToken token)
    {
        var user = await db.Users.Include(x => x.Wallet).SingleOrDefaultAsync(x => x.PhoneNumber == phone, token);
        if (user is not null) return user;
        user = new User
        {
            PhoneNumber = phone, DisplayName = name, Role = role, IsActive = true,
            PasswordHash = PasswordHash.Create("development-only"), Wallet = new Wallet { Currency = "YER" }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(token);
        return user;
    }

    private async Task EnsureDriverAsync(User user, ServiceKind kind, RideServiceCatalogItem service, string vehicle, string plate, decimal rating, CancellationToken token)
    {
        if (await db.DriverProfiles.AnyAsync(x => x.UserId == user.Id, token)) return;
        db.DriverProfiles.Add(new DriverProfile
        {
            UserId = user.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
            VehicleModel = vehicle, PlateNumber = plate, IsAvailable = true, Rating = rating, CommissionRate = .15m
        });
        await db.SaveChangesAsync(token);
    }

    private async Task EnsureLiveLocationAsync(User driver, double latitude, double longitude, CancellationToken token)
    {
        var location = await db.DriverLiveLocations.SingleOrDefaultAsync(x => x.DriverId == driver.Id, token);
        if (location is not null) return;
        db.DriverLiveLocations.Add(new DriverLiveLocation { DriverId = driver.Id, Latitude = latitude, Longitude = longitude, Bearing = 90, Speed = 6, IsOnline = true });
        await db.SaveChangesAsync(token);
    }

    private async Task EnsureSavedPlacesAsync(User customer, CancellationToken token)
    {
        if (await db.SavedPlaces.AnyAsync(x => x.UserId == customer.Id && x.Label == "المنزل التجريبي", token)) return;
        db.SavedPlaces.AddRange(
            new SavedPlaceEntity { UserId = customer.Id, Label = "المنزل التجريبي", Kind = "home", Address = "صنعاء - حدة", Latitude = 15.3694, Longitude = 44.1910, LocationKey = SavedPlaceLocationKey.Create(15.3694, 44.1910) },
            new SavedPlaceEntity { UserId = customer.Id, Label = "العمل التجريبي", Kind = "work", Address = "صنعاء - التحرير", Latitude = 15.3547, Longitude = 44.2067, LocationKey = SavedPlaceLocationKey.Create(15.3547, 44.2067) });
        await db.SaveChangesAsync(token);
    }

    private async Task EnsureServiceAreaAsync(CancellationToken token)
    {
        if (await db.ServiceAreas.AnyAsync(x => x.CountryCode == "YE" && x.CityNameAr == "صنعاء", token)) return;
        db.ServiceAreas.Add(new ServiceArea { CountryCode = "YE", CountryNameAr = "اليمن", CityNameAr = "صنعاء", IsActive = true });
        await db.SaveChangesAsync(token);
    }

    private async Task EnsurePricingAsync(ServiceKind kind, RideServiceCatalogItem service, decimal baseFare, decimal km, decimal minute, CancellationToken token)
    {
        if (await db.PricingRules.AnyAsync(x => x.ServiceKindId == kind.Id && x.ServiceCatalogItemId == service.Id, token)) return;
        db.PricingRules.Add(new PricingRule { ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, BaseFare = baseFare, PerKilometer = km, PerMinute = minute, DriverShareRate = .8m, IsActive = true });
        await db.SaveChangesAsync(token);
    }

    private async Task<Ride> EnsureRideAsync(User customer, ServiceKind kind, RideServiceCatalogItem service, string key, RideStatus status, User? driver, CancellationToken token)
    {
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.CustomerId == customer.Id && x.IdempotencyKey == key, token);
        if (ride is not null) return ride;
        ride = new Ride
        {
            CustomerId = customer.Id, DriverId = driver?.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
            IdempotencyKey = key, Status = status, PickupLabel = "انطلاق تجريبي", PickupAddress = "صنعاء - حدة",
            PickupLatitude = 15.3694, PickupLongitude = 44.1910, DestinationLabel = "وجهة تجريبية", DestinationAddress = "صنعاء - التحرير",
            DestinationLatitude = 15.3547, DestinationLongitude = 44.2067, CustomerPrice = service.BasePrice, ServerPrice = driver is null ? null : service.BasePrice
        };
        if (status == RideStatus.Completed) { ride.StartedAtUtc = DateTime.UtcNow.AddMinutes(-25); ride.CompletedAtUtc = DateTime.UtcNow.AddMinutes(-5); }
        db.Rides.Add(ride);
        await db.SaveChangesAsync(token);
        return ride;
    }

    private async Task EnsureOfferAsync(Ride ride, User driver, decimal amount, OfferStatus status, CancellationToken token)
    {
        if (await db.RideOffers.AnyAsync(x => x.RideId == ride.Id && x.DriverId == driver.Id, token)) return;
        db.RideOffers.Add(new RideOffer { RideId = ride.Id, DriverId = driver.Id, Amount = amount, Status = status, ExpiresAtUtc = status == OfferStatus.Pending ? DateTime.UtcNow.AddMinutes(10) : DateTime.UtcNow.AddMinutes(-5) });
        await db.SaveChangesAsync(token);
    }

    private async Task EnsureRideDataAsync(Ride ride, User customer, User driver, CancellationToken token)
    {
        if (!await db.LocationUpdates.AnyAsync(x => x.RideId == ride.Id, token))
            db.LocationUpdates.AddRange(
                new LocationUpdate { RideId = ride.Id, ActorId = customer.Id, Latitude = 15.3694, Longitude = 44.1910 },
                new LocationUpdate { RideId = ride.Id, ActorId = driver.Id, Latitude = 15.3630, Longitude = 44.1960 });
        if (!await db.CommunicationMessages.AnyAsync(x => x.RideId == ride.Id, token))
            db.CommunicationMessages.Add(new CommunicationMessage { RideId = ride.Id, SenderId = customer.Id, RecipientId = driver.Id, Content = "رسالة تجريبية داخل الرحلة", MessageType = "Text" });
        await db.SaveChangesAsync(token);
    }

    private async Task EnsureNotificationsAsync(User customer, User driver, Ride searching, Ride active, CancellationToken token)
    {
        if (await db.Notifications.AnyAsync(x => x.UserId == customer.Id && x.Title == "بيانات التطوير جاهزة", token)) return;
        db.Notifications.AddRange(
            new Notification { UserId = customer.Id, Type = NotificationType.System, Title = "بيانات التطوير جاهزة", Body = "أُضيفت رحلات وخدمات تجريبية لتجربة التطبيق.", DataJson = $"{{\"rideId\":{searching.Id}}}" },
            new Notification { UserId = driver.Id, Type = NotificationType.RideStatus, Title = "رحلة تجريبية نشطة", Body = "يمكنك تجربة تحديث موقع وحالة الرحلة.", DataJson = $"{{\"rideId\":{active.Id}}}" });
        await db.SaveChangesAsync(token);
    }
}
