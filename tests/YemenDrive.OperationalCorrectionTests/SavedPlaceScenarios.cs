using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Places;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using PlaceService = YemenDrive.Application.Places.SavedPlace;

static class SavedPlaceScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config)
    {
        var user = new User { PhoneNumber = "967711333001", DisplayName = "Places test" };
        var other = new User { PhoneNumber = "967711333002" }; db.AddRange(user, other); await db.SaveChangesAsync();
        var service = new PlaceService(db, config, new Actor(user.Id));
        async Task<ApiResult> Save(string kind, string label, double lat, double lng) => await service.ExecuteAsync("add", JsonSerializer.SerializeToElement(new SavedPlaceModel(Label: label, Kind: kind, Address: "العنوان", Latitude: lat, Longitude: lng)), default);
        var home = await Save("home", "قديم", 15.3, 44.1); Ok(home); var id = Data(home).GetProperty("id").GetInt32();
        var moved = await Save("home", "جديد", 15.4, 44.2); Ok(moved);
        Is(Data(moved).GetProperty("id").GetInt32() == id && await db.SavedPlaces.CountAsync(x => x.UserId == user.Id && x.Kind == "home") == 1, "reusing home updates coordinates without duplicate");
        Is(Data(moved).GetProperty("label").GetString() == "المنزل" && Data(moved).GetProperty("latitude").GetDouble() == 15.4, "standard kind supplies name and fresh coordinates");
        var custom = await Save("place", "  مكتب   أحمد  ", 15.5, 44.3); Ok(custom);
        var customMoved = await Save("place", "مكتب أحمد", 15.6, 44.4); Ok(customMoved);
        Is(Data(custom).GetProperty("id").GetInt32() == Data(customMoved).GetProperty("id").GetInt32(), "custom name is normalized and updates existing favourite");
        Ok(await Save("work", "", 15.4, 44.2));
        Ok(await Save("unnamed", "", 15.4, 44.2)); Ok(await Save("unnamed", "", 15.7, 44.5));
        Is(await db.SavedPlaces.CountAsync(x => x.UserId == user.Id && x.Latitude == 15.4 && x.Longitude == 44.2) == 3, "home, work and unnamed favourite can share coordinates independently");
        Is(await db.SavedPlaces.CountAsync(x => x.UserId == user.Id && x.Kind == "unnamed" && x.Label == "") == 2, "unnamed destinations remain separate favourites without shortcut labels");
        Is(!(await Save("place", "", 15.4, 44.2)).Success && !(await Save("home", "", 999, 44.2)).Success && !(await Save("home", "", 0, 0)).Success, "empty custom names and invalid coordinates rejected");
        var foreign = await new PlaceService(db, config, new Actor(other.Id)).ExecuteAsync("update", JsonSerializer.SerializeToElement(new SavedPlaceModel(Id: id, Latitude: 15.9)), default);
        Is(!foreign.Success && await db.SavedPlaces.Where(x => x.Id == id).Select(x => x.Latitude).SingleAsync() == 15.4, "another account cannot change a favourite");
        var connection = db.Database.GetConnectionString();
        async Task<ApiResult> Concurrent(double lat) {
            await using var ctx = new YemenDriveDbContext(new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options, RealtimeScenarioMode.Settings);
            return await new PlaceService(ctx, config, new Actor(user.Id)).ExecuteAsync("add", JsonSerializer.SerializeToElement(new SavedPlaceModel(Label: "متزامن", Kind: "place", Address: "العنوان", Latitude: lat, Longitude: 44.6)), default);
        }
        var concurrent = await Task.WhenAll(Concurrent(15.7), Concurrent(15.8)); db.ChangeTracker.Clear();
        Is(concurrent.Any(x => x.Success) && await db.SavedPlaces.CountAsync(x => x.UserId == user.Id && x.Label == "متزامن") == 1, "concurrent saves cannot create two shortcuts for one name");
        Console.WriteLine("PASS: saved destination names, unnamed favourites, ownership, coordinates and concurrent updates.");
    }
    static JsonElement Data(ApiResult r) => JsonSerializer.SerializeToElement(r.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static void Ok(ApiResult r) { if (!r.Success) throw new Exception(r.Code + ": " + r.Message); }
    static void Is(bool v, string label) { if (!v) throw new Exception(label); Console.WriteLine("PASS: saved places: " + label); }
    sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
