using System;
using FarmProgress;

// Run the production adapter against a simulated native API; fail if monitoring ever performs work.
public class PlayerScript { }
namespace Logic.Farm
{
    public enum WorkType { Refill, Harvest }
    public enum FailedActionReason { None, NotPermitted, NotEnoughMoney, AnimalHasEnoughFood }
    public class AnimalDefinition { public uint FoodCapacity = 10; public int FeedMoney = 100; }
    public class FarmData
    {
        public int Money = 100;
        public bool CanPay(int price, out FailedActionReason reason)
        { reason = Money >= price ? FailedActionReason.None : FailedActionReason.NotEnoughMoney; return Money >= price; }
    }
    public class FarmTile { public FarmData FarmData = new FarmData(); }
    public class AnimalField
    {
        public class FieldRaceData
        {
            public AnimalDefinition Definition = new AnimalDefinition();
            public uint Food = 100, ExtraFood, MaxFood = 100;
            public uint RemainingFoodCapacity { get { return Food > MaxFood ? 0 : MaxFood - Food; } }
        }
    }
}
namespace Logic.Farm.Contents
{
    public class AnimalFieldContents
    {
        public Logic.Farm.FarmTile Tile = new Logic.Farm.FarmTile();
        public bool NativeAllowed = true;
        public Logic.Farm.FailedActionReason NativeFailure;
        public int Checks;
        public PlayerScript CheckedPlayer;
        public bool Work(PlayerScript player, bool performWork, Logic.Farm.WorkType type, out Logic.Farm.FailedActionReason failure, int rotation = -1)
        {
            if (performWork || type != Logic.Farm.WorkType.Refill) throw new Exception("monitor attempted a farm mutation or wrong native action");
            Checks++; CheckedPlayer = player; failure = NativeFailure; return NativeAllowed;
        }
    }
}
class AnimalFeedingTests
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static int Main()
    {
        try
        {
            var animal = new Logic.Farm.Contents.AnimalFieldContents();
            var race = new Logic.Farm.AnimalField.FieldRaceData(); var player = new PlayerScript();
            Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "full trough");
            race.Food = 91; Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "one unit short of a full load");
            race.Food = 90; Check(AnimalFeeding.Read(animal, race, player).CanFeed, "exactly one full load available");
            Check(animal.Checks == 1 && ReferenceEquals(animal.CheckedPlayer, player), "read-only native validation uses the actual local player");
            race.Food = 0; Check(AnimalFeeding.Read(animal, race, player).CanFeed, "empty trough");
            race.ExtraFood = 200; Check(AnimalFeeding.Read(animal, race, player).CanFeed, "feeder reserve does not replace native trough-capacity calculation");
            race.Food = 101; Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "overfull trough cannot accept a load");
            race.Food = 0; animal.NativeAllowed = false; animal.NativeFailure = Logic.Farm.FailedActionReason.NotPermitted;
            var status = AnimalFeeding.Read(animal, race, player);
            Check(!status.CanFeed && status.BlockedReason.Contains("权限"), "native visitor permissions respected");
            animal.NativeFailure = Logic.Farm.FailedActionReason.NotEnoughMoney;
            Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "native payment refusal respected");
            animal.NativeFailure = Logic.Farm.FailedActionReason.AnimalHasEnoughFood;
            Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "other native refusal respected");
            animal.NativeAllowed = true; animal.Tile.FarmData.Money = 99;
            Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "cheap race elsewhere in a mixed pen cannot validate an unaffordable race");
            animal.Tile.FarmData.Money = 100;
            Check(AnimalFeeding.Read(animal, race, player).CanFeed, "exact feed price is affordable");
            Check(!AnimalFeeding.Read(animal, race, null).CanFeed, "no local player cannot validate native permissions");
            race.Definition.FoodCapacity = 0;
            Check(!AnimalFeeding.Read(animal, race, player).CanFeed, "zero-size load cannot become actionable");
            Check(race.Food == 0 && race.ExtraFood == 200 && animal.Tile.FarmData.Money == 100, "monitoring leaves feed, reserve and money unchanged");
            Console.WriteLine(checks + " native feeding adapter checks passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
