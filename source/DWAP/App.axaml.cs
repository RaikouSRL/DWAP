using Archipelago.Core;
using Archipelago.Core.AvaloniaGUI.Models;
using Archipelago.Core.AvaloniaGUI.ViewModels;
using Archipelago.Core.AvaloniaGUI.Views;
using Archipelago.Core.Helpers;
using Archipelago.Core.Json;
using Archipelago.Core.Models;
using Archipelago.Core.Util;
using Archipelago.Core.Util.GPS;
using Archipelago.Core.Util.PlatformMemory;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using DWAP.Models;
using DWAP.ViewModels;
using DWAP.Views;
using Newtonsoft.Json;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;


namespace DWAP;

public partial class App : Application
{
    static MainWindowViewModel Context;
    public static ArchipelagoClient Client { get; set; }
    public static PositionData CurrentLocation { get; set; }
    public static int StatCap { get; set; }
    private static readonly Queue<int> _pendingItems = new Queue<int>();
    private static readonly object _itemQueueLock = new object();
    private static long _pendingMoney = 0;
    // Archipelago.Core's ItemManager.ItemReceived event does not fire
    // reliably (confirmed: souls are received fine via polling
    // Client.CurrentSession.Items.AllItemsReceived, but consumables sent
    // via the server never triggered ItemReceived). So instead of relying
    // on that event, we poll the session's full received-items list every
    // tick and track how far we've processed it - the same reliable
    // approach the existing soul-handling code already uses.
    private static int _processedItemIndex = 0;
    private static readonly object _processedItemLock = new object();
    // Where the processed-item count for the CURRENT seed+slot is persisted,
    // so restarting/reconnecting doesn't replay already-applied consumables
    // and money (that replay isn't idempotent, unlike souls/log entries).
    private static string _itemIndexFilePath;
    // Prosperity thresholds already sent to the server this session - see
    // EnsureProsperity() for why these are sent manually instead of via
    // MonitorLocationsAsync.
    private static readonly HashSet<long> _completedProsperityLocationIds = new HashSet<long>();
    private static readonly HashSet<long> _completedWildDigimonLocationIds = new HashSet<long>();
    // Backs the "Recruitment Tracker" custom control (opened via the main
    // window's "Open Custom Controls" button) - one row per recruitable
    // Digimon, refreshed every tick by EnsureRecruitmentTracker().
    private static RecruitmentTrackerViewModel _recruitmentTrackerViewModel;
    public static int ExpMultiplier { get; set; }
    public static bool StatCapEnabled { get; set; }
    public static Randomiser RandomSettings { get; set; }
    private System.Timers.Timer _timer1 { get; set; } = new System.Timers.Timer(TimeSpan.FromSeconds(5));
    private static readonly object _lockObject = new object();
    private bool _fastDrimogemon = false;
    private bool _easyMonochromon = false;
    private enum GoalMode { Prosperity, Digitamamon, BeatTheGame }
    private GoalMode _goalMode = GoalMode.Prosperity;
    private int _requiredProsperity = 100;
    private ILocation goalLocation;
    // Only used in Beat-the-Game mode (goalSetting == 2). Reaching the
    // chosen Prosperity threshold in-game unlocks the Machinedramon fight,
    // but doesn't require actually winning it - "Beaten the game once
    // already" (Cards & Triggers ID 50, 0x001bdfd3 bit 2) is set right
    // after that fight is won, so goal completion in that mode requires
    // both goalLocation (the prosperity threshold) AND this to be true.
    private static readonly ILocation _machinedramonDefeatedLocation =
        LocationJsonHelper.Instance.DeserializeLocations(
            "[{\"Name\":\"Beaten The Game\",\"Id\":1,\"Address\":\"0x001bdfd3\",\"AddressBit\":2}]"
        ).Single();
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Start();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Context
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainWindow
            {
                DataContext = Context
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
    private void Start()
    {
        Context = new MainWindowViewModel("0.6.4");
        Context.ClientVersion = Assembly.GetEntryAssembly().GetName().Version.ToString();
        _timer1.Elapsed += TimerTick;
        Context.ConnectClicked += Context_ConnectClicked;
        Context.CommandReceived += (e, a) =>
        {
            Client?.SendMessage(a.Command);
        };

        Context.ConnectButtonEnabled = true;
        Context.UnstuckClicked += (o, e) =>
        {
        };
        Context.UnstuckButtonEnabled = true;
        Log.Information("Initialising collections...");
        Log.Information("Loading Items");
        Helpers.APItems = Helpers.GetAPItems();
        Log.Information("Loading Souls");
        Helpers.DigimonSouls = Helpers.GetDigimonSouls();
        Helpers.DigimonItems = Helpers.GetConsumables();

        var recruitableDigimonNames = Helpers.GetLocations().Select(x => x.Name);
        var wildEncounterNames = Helpers.GetWildDigimonLocations()
            .Select(x => (x.Name, Helpers.GetWildEncounterSpriteKey(x.Name)));
        _recruitmentTrackerViewModel = new RecruitmentTrackerViewModel(recruitableDigimonNames, wildEncounterNames);
        Context.CustomControlsWindow = new RecruitmentTrackerWindow
        {
            DataContext = _recruitmentTrackerViewModel
        };

        Log.Information("Ready to connect!");
    }
    private void AddDigimonItem()
    {
        // Process every item currently queued, not just one, so a burst of
        // items received between ticks doesn't overwrite/lose any of them.
        while (true)
        {
            int itemId;
            lock (_itemQueueLock)
            {
                if (_pendingItems.Count == 0)
                    return;
                itemId = _pendingItems.Dequeue();
            }
            AddSingleDigimonItem(itemId);
        }
    }

    private int GetMaxInventorySize()
    {
        // Base inventory is 10 unique item slots. "Unlocked Inventory Addon
        // #1"/"#2" (Cards & Triggers ID 47/48) each add another 10, for up
        // to 30 with both. The slot-index loops below previously stopped
        // at a hardcoded 10 regardless of these flags, so anything past
        // the base 10 slots was always sent to the item bank even when
        // real in-game capacity was 20 or 30.
        var maxSize = 10;
        var addonOneFlags = Memory.ReadByte(Addresses.InventoryAddonOneUnlocked);
        if ((addonOneFlags & 0x80) != 0) // bit 7
        {
            maxSize += 10;
        }
        var addonTwoFlags = Memory.ReadByte(Addresses.InventoryAddonTwoUnlocked);
        if ((addonTwoFlags & 0x01) != 0) // bit 0
        {
            maxSize += 10;
        }
        return maxSize;
    }

    private void AddSingleDigimonItem(int itemId)
    {
        var localId = itemId - 692000;
        var consumable = Helpers.DigimonItems.FirstOrDefault(x => x.Id == localId);
        if (consumable == null)
        {
            Log.Warning($"Received unknown consumable item id {itemId} (localId {localId}) - ignoring");
            return;
        }
        var maxInventorySize = GetMaxInventorySize();
        //Get matching item pile in inventory
        for (int i = 0; i < maxInventorySize; i++)
        {
            ulong slotAddress = (ulong)(0x0013D474 + i);
            ulong amountAddress = (ulong)(0x0013D492 + i);
            var currentItemInSlot = Memory.ReadByte(slotAddress);
            if (currentItemInSlot == consumable.Id)
            {
                var currentAmount = (int)Memory.ReadByte(amountAddress);
                Memory.WriteByte(amountAddress, (byte)(currentAmount + 1));
                return;
            }
        }

        //Item not already in inventory, find empty space
        var invSlot = GetEmptyInventorySlot();
        if (invSlot != null)
        {
            Memory.WriteByte(invSlot.Item1, (byte)localId);
            Memory.WriteByte(invSlot.Item2, 1);
            return;
        }

        //add to item bank
        ulong itemBankAddress = Addresses.ItemBankBaseAddress + (ulong)localId;
        var storedItemCount = Memory.ReadByte(itemBankAddress);
        if (storedItemCount == 255)
        {
            Memory.WriteByte(itemBankAddress, 1);
        }
        else
        {
            Memory.WriteByte(itemBankAddress, (byte)(Memory.ReadByte(itemBankAddress) + 1));
        }
    }
    private Tuple<ulong, ulong> GetEmptyInventorySlot()
    {
        var maxInventorySize = GetMaxInventorySize();
        for (int i = 0; i < maxInventorySize; i++)
        {
            ulong slotAddress = (ulong)(0x0013D474 + i);
            ulong amountAddress = (ulong)(0x0013D492 + i);
            if (Memory.ReadByte(slotAddress) == 255)
            {
                return new Tuple<ulong, ulong>(slotAddress, amountAddress);
            }
        }
        return null;
    }
    private void AddMoney()
    {
        // Atomically grab and clear the accumulated pending money so that
        // any money items received concurrently (between reading and
        // resetting) aren't lost.
        var amount = Interlocked.Exchange(ref _pendingMoney, 0);
        if (amount != 0)
        {
            var currentCash = Memory.ReadInt(Addresses.CurrentBits);
            var newCash = currentCash + (int)amount;
            Memory.Write(Addresses.CurrentBits, newCash);
        }
    }
    private async Task ConfigureOptions(Dictionary<string, object> options)
    {
        int randomSeed = 0;
        foreach (char c in Client.CurrentSession.RoomState.Seed)
        {
            randomSeed += Convert.ToInt32(c);
        }
        randomSeed += Client.CurrentSession.ConnectionInfo.Slot;
        var randomOptions = new RandomiserOptions(randomSeed);
        RandomSettings = new Randomiser(randomOptions);
        Log.Logger.Information("Running Randomisation");
        if (options.ContainsKey("easy_monochromon"))
        {
            _easyMonochromon = Convert.ToInt32(options["easy_monochromon"].ToString()) > 0;
        }
        if (options.ContainsKey("fast_drimogemon"))
        {
            _fastDrimogemon = Convert.ToInt32(options["fast_drimogemon"].ToString()) > 0;
        }
        if (options.ContainsKey("random_starter"))
        {
            var starterOption = Convert.ToInt32(options["random_starter"].ToString());
            if (starterOption == 0)
            {
                randomOptions.StarterRandomisation = StarterRandomisation.Vanilla;
            }
            else if (starterOption == 1)
            {
                randomOptions.StarterRandomisation = StarterRandomisation.All;
            }
            else if (starterOption == 2)
            {
                randomOptions.StarterRandomisation = StarterRandomisation.RookieOnly;
            }
        }
        RandomSettings.Generate();
        if (options.ContainsKey("exp_multiplier"))
        {
            ExpMultiplier = Convert.ToInt32(options["exp_multiplier"].ToString());
        }
        if (options.ContainsKey("progressive_stats") && Convert.ToInt32(options["progressive_stats"].ToString()) > 0)
        {
            StatCapEnabled = true;
            var boostsReceived = (Client.CurrentSession.Items.AllItemsReceived.Count(x => x.ItemName.ToLower() == "progressive stat cap"));
            if (boostsReceived >= 9)
            {
                StatCap = 999;
            }
            else StatCap = (boostsReceived * 100) + 100;
        }
        //if (options.ContainsKey("random_techniques"))
        //{
        //    _ = Task.Run(async () =>
        //    {
        //        try
        //        {
        //            await WaitForJijimonIntro();
        //            WriteLine("Randomising Technique Data");
        //            DigimonTechniques = RandomSettings.ShuffleAndWriteTechniques(DigimonTechniques);
        //        }
        //        catch (Exception ex)
        //        {
        //            WriteLine(ex.Message);
        //        }
        //    }).ConfigureAwait(false);
        //}
        if (options.ContainsKey("random_starter") && Convert.ToInt32(options["random_starter"].ToString()) > 0)
        {
            Log.Information("Writing new Starters");
            Memory.WriteByte(Addresses.Starter1, RandomSettings.Starter);
            Memory.WriteByte(Addresses.Starter2, RandomSettings.Starter);

            Log.Information("Checking for existing moveset");
            if (!CheckForMoves())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Helpers.WaitForJijimonIntroAsync();
                        WriteStarterMove(RandomSettings.Starter);
                    }
                    catch (Exception ex)
                    {
                        LogException("WriteStarterMove", ex);
                    }
                }).ConfigureAwait(false);
            }

        }
        if (options.ContainsKey("goal"))
        {
            var goalSetting = Convert.ToInt32(options["goal"].ToString());
            if (goalSetting == 0)
            {
                _goalMode = GoalMode.Prosperity;
                if (options.ContainsKey("required_prosperity"))
                {
                    var prosperityRequired = Convert.ToInt32(options["required_prosperity"].ToString());
                    _requiredProsperity = prosperityRequired;
                    goalLocation = Helpers.GetProsperityLocations().Single(x => x.Name == $"{prosperityRequired} Prosperity");
                }
                else
                {
                    _requiredProsperity = 100;
                    goalLocation = Helpers.GetProsperityLocations().Single(x => x.Name == "100 Prosperity");
                }
            }
            else if (goalSetting == 1)
            {
                _goalMode = GoalMode.Digitamamon;
                goalLocation = Helpers.GetLocations().Single(x => x.Name == "Digitamamon");        
            }
            else if (goalSetting == 2)
            {
                _goalMode = GoalMode.BeatTheGame;
                var prosperityRequired = options.ContainsKey("required_prosperity")
                    ? Convert.ToInt32(options["required_prosperity"].ToString())
                    : 100;
                // Beating Machinedramon only ever needs the native
                // 50-point requirement - a higher configured value is
                // capped here to match the apworld's own
                // completion_condition/mt_infinity_threshold, so there's
                // no reason to grind further than what's actually needed
                // to reach and beat him.
                _requiredProsperity = Math.Min(50, prosperityRequired);
                goalLocation = Helpers.GetProsperityLocations().Single(x => x.Name == $"{_requiredProsperity} Prosperity");
            }
        }
        else
        {
            _goalMode = GoalMode.Prosperity;
            if (options.ContainsKey("required_prosperity"))
            {
                var prosperityRequired = Convert.ToInt32(options["required_prosperity"].ToString());
                _requiredProsperity = prosperityRequired;
                goalLocation = Helpers.GetProsperityLocations().Single(x => x.Name == $"{prosperityRequired} Prosperity");
            }
            else
            {
                _requiredProsperity = 100;
                goalLocation = Helpers.GetProsperityLocations().Single(x => x.Name == "100 Prosperity");
            }
        }

    }

    public bool CheckForMoves()
    {
        bool hasMoves = !(Memory.ReadByte(0x00155800) == 0
            && Memory.ReadByte(0x00155805) == 0
            && Memory.ReadByte(0x00155801) == 0
            && Memory.ReadByte(0x00155804) == 0
            && Memory.ReadByte(0x00155802) == 0
            && Memory.ReadByte(0x00155803) == 0
            && Memory.ReadByte(0x00155806) == 0);
        if (hasMoves)
        {
            Log.Information("Moves detected");
        }
        else Log.Warning("No moves");
        return hasMoves;
    }
    private void WriteStarterMove(byte starter)
    {
        Log.Information("Setting Starter Technique");
        var move = Helpers.GetStarterMove(starter);

        Memory.WriteBit(move.Address, move.AddressBit, true);

        Memory.WriteByte(Addresses.TechniqueSlot1, 46);
    }
    private List<DigimonTechniqueData> ReadTechniques()
    {
        Log.Information("Reading Technique Data");
        List<DigimonTechniqueData> techniques = new List<DigimonTechniqueData>();
        ulong currentAddress = Addresses.TechniqueStartAddress;
        ulong learningChanceAddress = Addresses.LearningChanceStartAddress;
        for (int i = 0; i < 120; i++)
        {
            DigimonTechniqueData tech = new DigimonTechniqueData();
            tech.Slot = i;
            tech.Unknown1 = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.Unknown2 = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.AITargetDistance = Memory.ReadShort(currentAddress);
            currentAddress += Addresses.ShortOffset;
            tech.Power = Memory.ReadShort(currentAddress);
            currentAddress += Addresses.ShortOffset;
            tech.MP = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.IFrames = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.Range = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.Type = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.StatusEffect = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.BlockingFactor = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.StatusChance = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.Unknown3 = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.Unknown4 = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.Unknown5 = Memory.ReadByte(currentAddress);
            currentAddress += Addresses.ByteOffset;
            tech.LearningChance1 = Memory.ReadByte(learningChanceAddress);
            learningChanceAddress += Addresses.ByteOffset;
            tech.LearningChance2 = Memory.ReadByte(learningChanceAddress);
            learningChanceAddress += Addresses.ByteOffset;
            tech.LearningChance3 = Memory.ReadByte(learningChanceAddress);
            learningChanceAddress += Addresses.ByteOffset;
            tech = tech.PopulateTechData();
            techniques.Add(tech);
        }
        Log.Information("Techniques Loaded");
        return techniques;
    }


    private void SetExpMultiplier(int multiplier)
    {
        var multVal = multiplier * 10;
        Memory.Write(0x001384AE, multVal);

        Memory.WriteByte(0x001384AC, 63);
        Memory.Write(0x001384B0, (short)9999);
    }

    private void EnsureStatCap()
    {
        if (!StatCapEnabled) { return; }
        var boostsReceived = (Client.CurrentSession.Items.AllItemsReceived.Count(x => x.ItemName.ToLower() == "progressive stat cap"));
        if (boostsReceived >= 9)
        {
            StatCap = 999;
        }
        else StatCap = (boostsReceived * 100) + 100;
        var currHpMax = Memory.ReadShort(Addresses.MaxHp);
        var currMpMax = Memory.ReadShort(Addresses.MaxMp);
        var currOff = Memory.ReadShort(Addresses.CurrentOffense);
        var currDef = Memory.ReadShort(Addresses.CurrentDefence);
        var currSpd = Memory.ReadShort(Addresses.CurrentSpeed);
        var currBrn = Memory.ReadShort(Addresses.CurrentBrains);

        if (StatCap < 999)
        {
            Memory.Write(Addresses.MaxHp, (short)Math.Min(currHpMax, (short)(StatCap * 10)));
            Memory.Write(Addresses.MaxMp, (short)Math.Min(currMpMax, (short)(StatCap * 10)));
        }
        else
        {
            Memory.Write(Addresses.MaxHp, (short)Math.Min(currHpMax, (short)9999));
            Memory.Write(Addresses.MaxMp, (short)Math.Min(currMpMax, (short)9999));
        }
        Memory.Write(Addresses.CurrentOffense, (short)Math.Min(currOff, StatCap));
        Memory.Write(Addresses.CurrentDefence, (short)Math.Min(currDef, StatCap));
        Memory.Write(Addresses.CurrentSpeed, (short)Math.Min(currSpd, StatCap));
        Memory.Write(Addresses.CurrentBrains, (short)Math.Min(currBrn, StatCap));
    }
    private void TimerTick(object? sender, ElapsedEventArgs e)
    {
        // Guard the whole tick: an unhandled exception thrown from a
        // System.Timers.Timer callback can silently stop the timer (or
        // crash the process), which would stop all item/money/location
        // processing for the rest of the session. Each step is wrapped
        // individually (via RunStep) so one failing step doesn't prevent
        // the others from running this tick, and so the log tells us
        // exactly which step failed instead of just "something in
        // TimerTick threw".
        try
        {
            Log.Debug("TimerTick fired");
            if (ExpMultiplier > 1)
            {
                RunStep("SetExpMultiplier", () => SetExpMultiplier(ExpMultiplier));
            }
            CurrentLocation = Helpers.GetCurrentLocation();
            RunStep("EnsureStatCap", EnsureStatCap);
            RunStep("EnsureSouls", EnsureSouls);
            RunStep("EnsurePostGameSoulRecruits", EnsurePostGameSoulRecruits);
            RunStep("EnsureWorldFlags", EnsureWorldFlags);
            RunStep("EnsureProsperity", EnsureProsperity);
            RunStep("EnsureKoda01VermilimonWorkaround", EnsureKoda01VermilimonWorkaround);
            RunStep("EnsureWildDigimonChecks", EnsureWildDigimonChecks);
            RunStep("EnsureMtInfinityUnlock", EnsureMtInfinityUnlock);
            RunStep("EnsureRecruitmentTracker", EnsureRecruitmentTracker);
            RunStep("ProcessReceivedItems", ProcessReceivedItems);
            RunStep("AddMoney", AddMoney);
            RunStep("AddDigimonItem", AddDigimonItem);

            var goalReached = _goalMode == GoalMode.BeatTheGame
                ? (goalLocation?.Check() ?? false) && _machinedramonDefeatedLocation.Check()
                : (goalLocation?.Check() ?? false);
            if (goalReached)
            {
                Client.SendGoalCompletion();
            }
        }
        catch (Exception ex)
        {
            LogException("TimerTick", ex);
        }
    }

    private void RunStep(string stepName, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            LogException(stepName, ex);
        }
    }

    private static void LogException(string context, Exception ex)
    {
        // Bake the exception type/message/stack trace directly into the log
        // message text (rather than relying on Serilog's {Exception} token
        // in the sink's output template) so the detail can't get dropped or
        // truncated by whatever is rendering/viewing the log.
        Log.Logger.Error(
            "Unhandled exception in {Context}: {ExceptionType}: {ExceptionMessage}\n{StackTrace}",
            context, ex.GetType().FullName, ex.Message, ex.StackTrace);
    }

    private void EnsureWildDigimonChecks()
    {
        // Deliberately NOT handed to MonitorLocationsAsync: two Wild Digimon
        // "beaten" bits can flip true in the same fight (a mixed encounter),
        // and the generic monitor only ever sent one of the two as a check
        // when that happened - almost certainly because it tracks/dedupes
        // by address rather than by address+bit, so two locations sharing
        // one byte only get one check between them when both go true in
        // the same poll. Polling and completing each one ourselves here,
        // same pattern as EnsureProsperity, sidesteps that entirely - every
        // location gets its own independent Check() and its own send.
        foreach (var location in Helpers.GetWildDigimonLocations())
        {
            if (_completedWildDigimonLocationIds.Contains(location.Id))
                continue;
            if (location.Check())
            {
                Client.CurrentSession.Locations.CompleteLocationChecks(location.Id);
                _completedWildDigimonLocationIds.Add(location.Id);
            }
        }
    }

    private void EnsureProsperity()
    {
        var prosperity = Helpers.CalculateProsperityPoints(Client);
        Memory.Write(Addresses.ProsperityPoints, prosperity);

        // Send prosperity milestone checks ourselves, using only the
        // soul-gated value just computed above - see the comment where
        // GetProsperityLocations() is built (in Connect()) for why these are
        // deliberately NOT handed to MonitorLocationsAsync.
        foreach (var location in Helpers.GetProsperityLocations())
        {
            if (_completedProsperityLocationIds.Contains(location.Id))
                continue;
            var namePrefix = location.Name?.Split(' ')[0];
            if (!int.TryParse(namePrefix, out var threshold))
                continue;
            if (prosperity >= threshold)
            {
                Client.CurrentSession.Locations.CompleteLocationChecks(location.Id);
                _completedProsperityLocationIds.Add(location.Id);
            }
        }
    }

    private void EnsureMtInfinityUnlock()
    {
        // Mt. Infinity (and everything gated behind it - Devimon,
        // Airdramon, MetalGreymon, Megadramon, Piddomon, and ultimately
        // Machinedramon) only unlocks natively once real in-game Prosperity
        // reaches 50 - but the vanilla game also requires the player to
        // already own the Greymon Soul at that point, which isn't
        // guaranteed to be in logic/received yet in a randomised seed. So
        // reaching native 50 Prosperity forces the unlock flag directly
        // here, skipping that Soul requirement entirely, regardless of
        // goal mode.
        var currentProsperity = Memory.ReadByte(Addresses.ProsperityPoints);
        if (currentProsperity >= 50)
        {
            Memory.WriteBit(Addresses.MtInfinityUnlocked, 2, true);
            return;
        }

        // If the player's chosen Beat-the-Game threshold is lower than the
        // native 50, force the same unlock early at that lowered value too,
        // so the goal isn't impossible to complete without separately
        // grinding past it. Only relevant in Beat-the-Game mode.
        if (_goalMode != GoalMode.BeatTheGame || _requiredProsperity >= 50)
        {
            return;
        }
        if (currentProsperity >= _requiredProsperity)
        {
            Memory.WriteBit(Addresses.MtInfinityUnlocked, 2, true);
        }
    }

    private void EnsureWorldFlags()
    {
        if (_easyMonochromon && CurrentLocation.MapId == 49)
        {
            Memory.Write(Addresses.MonochromeProfitAddress, 4000);
        }
        if (_fastDrimogemon)
        {
            var hasBeatenDrimogemon = Memory.ReadByte(Addresses.HasBeatenDrimogemon);
            var meramonDigState = Memory.ReadByte(Addresses.MeramonTunnel_DiggingState);
            var meramonTunnelState = Memory.ReadByte(Addresses.MeramonTunnel_State);
            if (hasBeatenDrimogemon == 1 && ( meramonDigState != 5 && meramonTunnelState != 10))
            {
                Memory.WriteByte(Addresses.MeramonTunnel_DrimogemonState, 2); // set Drimogemon to already talked to
                Memory.WriteByte(Addresses.MeramonTunnel_State, 10);          // set tunnel as already dug
                Memory.WriteByte(Addresses.MeramonTunnel_DiggingState, 5);    // set pile as empty
            }
        }
    }

    private async void EnsureSouls()
    {
        var locations = Helpers.GetLocations();
        var souls = Helpers.GetMissingSouls(Client);
        foreach (var soul in souls)
        {
            var digimonName = soul.Name.Split(" ")[0];
            var recruitLocation = (Location)locations.FirstOrDefault(x => x.Name == digimonName);

            Memory.WriteBit(recruitLocation.Address, recruitLocation.AddressBit, false);
        }
    }

    // Once "Beaten The Game" (Cards & Triggers ID 50) is set, receiving one
    // of these Souls afterwards is enough on its own to mark the matching
    // Digimon as recruited directly - no need to re-climb Mt. Infinity and
    // fight it again post-game. Devimon = ID 206, Airdramon = ID 207,
    // MetalGreymon = ID 212 (all confirmed against Locations.json).
    private static readonly string[] _postGamePardonedRecruits = { "Devimon", "Airdramon", "MetalGreymon" };

    private void EnsurePostGameSoulRecruits()
    {
        // Before the game's been beaten, these still go through the normal
        // recruit-then-Soul flow (EnsureSouls() above still reverts the
        // recruit flag if the Soul isn't owned).
        if (!_machinedramonDefeatedLocation.Check())
        {
            return;
        }
        var acquiredSoulNames = Helpers.GetAcquiredSouls(Client).Select(x => x.Name.Split(' ')[0]).ToHashSet();
        var locations = Helpers.GetLocations();
        foreach (var name in _postGamePardonedRecruits)
        {
            if (!acquiredSoulNames.Contains(name))
            {
                continue;
            }
            var recruitLocation = (Location)locations.FirstOrDefault(x => x.Name == name);
            if (recruitLocation != null)
            {
                Memory.WriteBit(recruitLocation.Address, recruitLocation.AddressBit, true);
            }
        }
    }

    // Base-game bug: the wild encounter at map ID 80 (KODA01, "Entrance",
    // Glacial Region) can spawn either Yanmamon or Vermilimon, but winning
    // either fight always sets Yanmamon's "beaten" flag (0x001bdfee bit 3) -
    // Vermilimon's own flag (0x001bdff1 bit 0) never gets written there, so
    // its Wild Encounter check is otherwise unobtainable from that spawn.
    // Since nothing in memory tells us which of the two was actually
    // fought, we treat any fresh set of Yanmamon's flag while on map 80 as
    // covering both: clear Yanmamon's flag the moment the player steps onto
    // the map (a clean baseline for this visit, regardless of whether it
    // was already true from a fight elsewhere), then if it flips back to
    // true while still on the map, that's this map's encounter finishing -
    // award Vermilimon's flag too and leave Yanmamon's true so its own
    // check still sends normally via EnsureWildDigimonChecks(). Confirmed
    // fine to clear an already-true Yanmamon flag: the only consequence is
    // the AP check it already sent, which can't be un-sent anyway, and the
    // game has no other logic hanging off that flag.
    private const int Koda01MapId = 80;
    private int _koda01PreviousMapId = -1;

    private void EnsureKoda01VermilimonWorkaround()
    {
        var currentMapId = Helpers.GetCurrentLocation().MapId;
        var locations = Helpers.GetWildDigimonLocations();
        var yanmamonLocation = (Location)locations.FirstOrDefault(x => x.Name == "Yanmamon");
        var vermilimonLocation = (Location)locations.FirstOrDefault(x => x.Name == "Vermilimon");
        if (yanmamonLocation == null || vermilimonLocation == null)
        {
            _koda01PreviousMapId = currentMapId;
            return;
        }

        var justEntered = currentMapId == Koda01MapId && _koda01PreviousMapId != Koda01MapId;
        if (justEntered)
        {
            Memory.WriteBit(yanmamonLocation.Address, yanmamonLocation.AddressBit, false);
        }
        else if (currentMapId == Koda01MapId && yanmamonLocation.Check())
        {
            Memory.WriteBit(vermilimonLocation.Address, vermilimonLocation.AddressBit, true);
        }

        _koda01PreviousMapId = currentMapId;
    }

    private void EnsureRecruitmentTracker()
    {
        // Drives the "Recruitment Tracker" custom control window - both the
        // Recruitments grid and the Wild Encounters grid. Recruited and
        // Soul are tracked independently, same distinction EnsureSouls()
        // relies on: the in-game recruit flag (Locations.json Check()) can
        // briefly read true before its Soul item is actually owned, and
        // EnsureSouls() reverts that - the tracker mirrors both states as
        // they actually are rather than collapsing them into one.
        if (_recruitmentTrackerViewModel == null)
        {
            return;
        }
        var locations = Helpers.GetLocations();
        var acquiredSoulNames = Helpers.GetAcquiredSouls(Client).Select(x => x.Name.Split(' ')[0]).ToHashSet();
        foreach (var entry in _recruitmentTrackerViewModel.Recruitments)
        {
            var recruitLocation = locations.FirstOrDefault(x => x.Name == entry.Name);
            entry.IsRecruited = recruitLocation?.Check() ?? false;
            entry.HasSoul = acquiredSoulNames.Contains(entry.Name);
        }

        var wildLocations = Helpers.GetWildDigimonLocations();
        foreach (var entry in _recruitmentTrackerViewModel.WildEncounters)
        {
            var wildLocation = wildLocations.FirstOrDefault(x => x.Name == entry.Name);
            entry.IsDefeated = wildLocation?.Check() ?? false;
        }

        _recruitmentTrackerViewModel.RefreshSummary();
    }
    public async Task Connect(ConnectClickedEventArgs args)
    {
        if (Client != null)
        {
            Client.Connected -= OnConnected;
            Client.Disconnected -= OnDisconnected;
            Client.MessageReceived -= Client_MessageReceived;
        }
        GameClient client = new GameClient("duckstation");
        var duckstationConnected = client.Connect();
        if (!duckstationConnected)
        {
            Log.Warning("Duckstation not running, open Duckstation and launch the game before connecting!");
            return;
        }
        Client = new ArchipelagoClient(client);

        PlatformMemory.GlobalOffset = PlatformMemory.GetDuckstationOffset();

        Client.Connected += OnConnected;
        Client.Disconnected += OnDisconnected;
        // This was defined (Client_MessageReceived/LogHint below) but never
        // actually subscribed anywhere, so hint messages never reached the
        // Hints tab.
        Client.MessageReceived += Client_MessageReceived;

        await Client.Connect(args.Host, "Digimon World");
        Log.Information("Connected to Archipelago server, logging in...");

        await Client.Login(args.Slot, !string.IsNullOrWhiteSpace(args.Password) ? args.Password : null);
        Log.Information("Login successful, continuing setup...");

        // Load how many items we've already applied for THIS seed+slot from
        // disk, rather than starting at 0 - otherwise every restart/reconnect
        // would replay the full received-items history and duplicate
        // consumables/money that were already added on a previous run.
        lock (_processedItemLock)
        {
            _itemIndexFilePath = BuildItemIndexFilePath(args.Host, Client.CurrentSession.RoomState.Seed, args.Slot);
            _processedItemIndex = LoadProcessedItemIndex(_itemIndexFilePath);
        }
        Log.Debug($"Resuming item processing at index {_processedItemIndex}");

        // Reset per-connection dedupe state - stale entries from a
        // different seed/slot connected to earlier in the same app run
        // could otherwise suppress legitimate prosperity completions here.
        // Resending an already-completed location is harmless (the server
        // just ignores it), so starting empty each connect is safe.
        _completedProsperityLocationIds.Clear();
        _completedWildDigimonLocationIds.Clear();

        Helpers.DigimonTechniques = ReadTechniques();

#if DEBUG
        _ = Task.Run(async () =>
        {
            await Helpers.WaitForJijimonIntroAsync();
            var dumpPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "dw1_ram_dump.bin");
            Helpers.DumpPS1RAM(dumpPath);
        }).ConfigureAwait(false);
#endif

        var locations = Helpers.GetDigimonCards();
        locations.AddRange(Helpers.GetChests());
        // Prosperity locations are deliberately NOT included here. Their
        // Check() reads Addresses.ProsperityPoints directly, and the native
        // game writes to that address immediately and unconditionally the
        // instant ANY Digimon is recruited - including ones whose soul the
        // player doesn't own, where the recruit is supposed to not count.
        // MonitorLocationsAsync polls far faster than our EnsureProsperity()
        // correction cycle, so it can catch that raw, un-gated write and
        // send a false completion before we ever overwrite it with the
        // correct value. Instead, EnsureProsperity() below sends prosperity
        // checks itself, driven only by our soul-gated calculation.
        //
        // Wild Digimon locations are also deliberately NOT included here -
        // several share a single byte (multiple wild/glitch Digimon can be
        // fought in the same encounter, each with its own bit in the same
        // address), and MonitorLocationsAsync only ever sent one of them as
        // a check when two flipped true in the same poll. EnsureWildDigimonChecks()
        // below polls and completes each one individually instead.
        Client.LocationManager.EnableLocationsCondition = ()=> Helpers.IsInGame();

        Client.LocationManager.MonitorLocationsAsync(Client.CurrentSession, locations);
        Client.GPSHandler = new Archipelago.Core.Util.GPS.GPSHandler(() => Helpers.GetCurrentLocation());
        Client.GPSHandler.PositionChanged += (o, e) =>
        {
            Log.Verbose($"Position: {e.NewX} {e.NewY}");
        };
        Client.GPSHandler.MapChanged += (o, e) =>
        {
            Log.Debug($"Map Changed: {Client.GPSHandler.Region}: {e.NewMapName}");
        };
        _timer1.Start();
        Log.Information("Timer started");
        if (Client.Options != null)
        {
            await ConfigureOptions(Client.Options);
        }
        Client.ItemManager.ItemReceived += OnItemReceived;
        Log.Debug("Subscribed to ItemReceived - ready to receive items");

        //Is game started yet?
        if (!Client.CurrentSession.Locations.AllLocationsChecked.Any(x => x == 69003000))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Helpers.WaitForJijimonIntroAsync();
                    var startGameLocation = new Archipelago.Core.Models.Location() { Id = 69003000, Name = "Start Game" };
                    Client.CurrentSession.Locations.CompleteLocationChecks(startGameLocation.Id);
                }
                catch (Exception ex)
                {
                    LogException("StartGameLocationCheck", ex);
                }
            }).ConfigureAwait(false);
        }
        var soulLocations = new List<Archipelago.Core.Models.ILocation>();
        var acquiredSouls = Helpers.GetAcquiredSouls(Client);
        if (acquiredSouls.Any())
        {
            var recruitLocations = Helpers.GetLocations();
            foreach (var soul in acquiredSouls)
            {
                var digimonName = soul.Name.Split(" ")[0];
                var recruitLocation = recruitLocations.FirstOrDefault(x => x.Name == digimonName);
                soulLocations.Add(recruitLocation);
            }
            if (soulLocations.Any())
            {
                Client.LocationManager.MonitorLocationsAsync(Client.CurrentSession, soulLocations);
            }
        }
    }

    private static string BuildItemIndexFilePath(string host, string seed, string slot)
    {
        if (string.IsNullOrEmpty(seed))
        {
            // Silently falling back to a generic name here is exactly what
            // caused two different sessions to collide on the same index
            // file previously - make it loud instead.
            Log.Logger.Warning("Room seed was empty/null when building the item-index file path - including host+slot only, which is a weaker uniqueness guarantee than seed+slot.");
        }
        var safeHost = SanitizeForFileName(host);
        var safeSeed = SanitizeForFileName(seed);
        var safeSlot = SanitizeForFileName(slot);
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DWAP", "itemindex");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"{safeHost}_{safeSeed}_{safeSlot}.txt");
        Log.Logger.Information($"Item-index file for this session: {path} (host='{host}', seed='{seed}', slot='{slot}')");
        return path;
    }

    private static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "unknown";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static int LoadProcessedItemIndex(string path)
    {
        try
        {
            if (System.IO.File.Exists(path) && int.TryParse(System.IO.File.ReadAllText(path).Trim(), out var index))
            {
                return index;
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Warning("Failed to read processed-item index from {Path}, starting from 0: {ExceptionType}: {ExceptionMessage}", path, ex.GetType().FullName, ex.Message);
        }
        return 0;
    }

    private static void SaveProcessedItemIndex(string path, int index)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            System.IO.File.WriteAllText(path, index.ToString());
        }
        catch (Exception ex)
        {
            Log.Logger.Warning("Failed to persist processed-item index to {Path}: {ExceptionType}: {ExceptionMessage}", path, ex.GetType().FullName, ex.Message);
        }
    }

    private void ProcessReceivedItems()
    {
        // See the field comment above: ItemManager.ItemReceived is not
        // reliable, so this polls the session's full received-items list
        // (the same underlying data EnsureSouls()/GetAcquiredSouls() already
        // use successfully) and processes anything new since last tick.
        if (!Helpers.IsInGame() || !Helpers.IsTimeRunning() || !Helpers.HasGainedControl())
        {
            // Defer entirely while not actually in a loaded game (e.g.
            // connected before starting/loading a save, or still in the
            // opening cutscenes) - memory addresses for inventory/cash
            // aren't valid yet, so writing to them here would silently do
            // nothing useful while still advancing the processed-item
            // index, permanently losing whatever was received during that
            // window. Nothing is marked processed until IsInGame() is true,
            // so once it is, everything received in the meantime (from the
            // server or queued up before connecting) gets applied on the
            // very next tick.
            // Also defer while Timespeed is stopped (>2), and - critically -
            // while HasGainedControl() is false. IsInGame()/IsTimeRunning()
            // both already read as "ready" during the character/partner
            // naming screen (confirmed: currentTime is already >8 and
            // Timespeed stays 0 there), so HasGainedControl() is the one
            // that actually catches that window and holds items until the
            // intro cutscene finishes and control is handed over.
            Log.Debug("Not in game yet, time is stopped, or control not yet gained - deferring received-item processing");
            return;
        }
        var newItems = new List<(long ItemId, string ItemName)>();
        int totalCount;
        lock (_processedItemLock)
        {
            var allItems = Client.CurrentSession.Items.AllItemsReceived;
            totalCount = allItems.Count();
            Log.Debug($"ProcessReceivedItems: processedIndex={_processedItemIndex}, totalCount={totalCount}");
            if (_processedItemIndex > totalCount)
            {
                // The loaded index is higher than this session actually has
                // - it belongs to a different seed/slot than the one we're
                // connected to right now (a persisted-file collision, e.g.
                // from a seed that couldn't be read and fell back to a
                // shared name). An index greater than the real count can
                // never be valid, so reset rather than staying stuck
                // permanently skipping every item forever.
                Log.Warning($"Processed-item index ({_processedItemIndex}) exceeds this session's item count ({totalCount}) - resetting to 0. This usually means the saved index file didn't match this seed/slot.");
                _processedItemIndex = 0;
            }
            if (_processedItemIndex >= totalCount)
                return;
            foreach (var received in allItems.Skip(_processedItemIndex))
            {
                if (received != null)
                {
                    newItems.Add((received.ItemId, received.ItemName));
                }
            }
        }

        foreach (var received in newItems)
        {
            HandleReceivedItem(received.ItemId, received.ItemName);
        }

        // Only advance/persist the cursor AFTER the items above have been
        // successfully applied - if the app crashed mid-loop, we'd rather
        // reprocess an item on next launch (harmless for souls/log entries,
        // and at worst gives one duplicate consumable) than mark items as
        // "done" that never actually got written to game memory.
        lock (_processedItemLock)
        {
            _processedItemIndex = totalCount;
        }
        SaveProcessedItemIndex(_itemIndexFilePath, totalCount);
    }

    private void HandleReceivedItem(long itemId, string itemName)
    {
        Log.Debug($"Processing received item: {itemName} ({itemId})");
        // LogItem() populates the Received Items tab - it existed already
        // but was never actually called from anywhere, so the tab always
        // stayed empty regardless of what was received.
        LogItem(new Item() { Id = (int)itemId, Name = itemName });
        if (Helpers.APItems.Any(x => x.Id == itemId))
        {
            var item = Helpers.APItems.First(x => x.Id == itemId);
            if (item.Type == ItemType.Consumable || item.Type == ItemType.DV)
            {
                lock (_itemQueueLock)
                {
                    _pendingItems.Enqueue(item.Id);
                }
            }
            else if (item.Name == "1000 Bits")
            {
                Interlocked.Add(ref _pendingMoney, 1000);
            }
            else if (item.Name == "5000 Bits")
            {
                Interlocked.Add(ref _pendingMoney, 5000);
            }
            else if (item.Name == "Progressive Stat Cap")
            {
                var boostsReceived = (Client.CurrentSession.Items.AllItemsReceived.Count(x => x.ItemName.ToLower() == "progressive stat cap"));
                if (boostsReceived >= 9)
                {
                    StatCap = 999;
                }
                else StatCap = (boostsReceived * 100) + 100;
            }
        }
        else if (Helpers.DigimonSouls.Any(x => x.Id == itemId))
        {
            var item = Helpers.DigimonSouls.First(x => x.Id == itemId);
            if (item.Type == ItemType.Soul)
            {
                var soulName = item.Name.Split(" ")[0];
                var digimonRecruit = Helpers.GetLocations().Where(x => x.Name.Contains(soulName)).ToList();
                Client.LocationManager.MonitorLocationsAsync(Client.CurrentSession, digimonRecruit);
            }
        }
    }

    private void OnItemReceived(object? sender, ItemReceivedEventArgs args)
    {
        // Kept subscribed only for diagnostic visibility - ItemManager's
        // event has proven unreliable (see field comment above), so all
        // actual item handling now happens via ProcessReceivedItems()
        // polling instead. Do not add state-changing logic back in here
        // without also removing it from HandleReceivedItem, or items will
        // get double-applied on the occasions this event does fire.
        Log.Debug($"ItemManager.ItemReceived fired: {JsonConvert.SerializeObject(args.Item)}");
    }
    private void Context_ConnectClicked(object? sender, ConnectClickedEventArgs e)
    {
        if (Client == null || !(Client?.IsConnected ?? false))
        {
            // Connect() is async and was previously fired-and-forgotten with
            // ConfigureAwait(false) and no error handling: any exception
            // partway through setup (before the timer is started and
            // ItemReceived is subscribed, near the end of Connect()) was
            // silently swallowed - items/locations would then never work,
            // with no indication anything had gone wrong.
            _ = ConnectSafely(e);
        }
        else if (Client != null)
        {
            Log.Information("Disconnecting...");
            Client.Disconnect();
        }
    }

    private async Task ConnectSafely(ConnectClickedEventArgs e)
    {
        try
        {
            await Connect(e);
        }
        catch (Exception ex)
        {
            LogException("ConnectSafely", ex);
        }
    }
    private static void LogItem(Item item)
    {
        // SolidColorBrush is an Avalonia UI object (Animatable ->
        // AvaloniaObject) and can only be constructed on the UI thread.
        // This is now called from TimerTick's background timer thread via
        // ProcessReceivedItems/HandleReceivedItem, so the brushes (and the
        // TextSpans/LogListItem that hold them) must be built INSIDE the
        // scheduled callback below, not before it - building them here on
        // the calling thread throws "Call from invalid thread".
        lock (_lockObject)
        {
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                var messageToLog = new LogListItem(new List<TextSpan>()
                    {
                        new TextSpan(){Text = $"[{item.Id.ToString()}] -", TextColor = new SolidColorBrush(Color.FromRgb(255, 255, 255))},
                        new TextSpan(){Text = $"{item.Name}", TextColor = new SolidColorBrush(Color.FromRgb(200, 255, 200))},
                    });
                Context.ItemList.Add(messageToLog);
            });
        }
    }

    private void Client_MessageReceived(object? sender, Archipelago.Core.Models.MessageReceivedEventArgs e)
    {
        if (e.Message.Parts.Any(x => x.Text == "[Hint]: "))
        {
            LogHint(e.Message);
        }
        // Server broadcasts (item sends, chat, etc.) only ever get logged
        // here - there's no separate UI list for them like there is for
        // locally-received items (LogItem/Context.ItemList) - so this needs
        // to stay readable at Information, e.g. "Digimon World found their
        // DigiTrout". The full raw payload is still available at Debug.
        var readableMessage = string.Join("", e.Message.Parts.Select(x => x.Text));
        Log.Information(readableMessage);
        Log.Debug(JsonConvert.SerializeObject(e.Message));
    }
    private static void LogHint(LogMessage message)
    {
        var newMessage = message.Parts.Select(x => x.Text);

        if (Context.HintList.Any(x => x.TextSpans.Select(y => y.Text).SequenceEqual(newMessage)))
        {
            return; //Hint already in list
        }
        // Same threading issue as LogItem above - build the SolidColorBrush
        // objects inside the scheduled UI-thread callback, not before it.
        lock (_lockObject)
        {
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                List<TextSpan> spans = new List<TextSpan>();
                foreach (var part in message.Parts)
                {
                    spans.Add(new TextSpan() { Text = part.Text, TextColor = new SolidColorBrush(Color.FromRgb(part.Color.R, part.Color.G, part.Color.B)) });
                }
                Context.HintList.Add(new LogListItem(spans));
            });
        }
    }
    private static void OnConnected(object sender, EventArgs args)
    {
        Log.Information("Connected to Archipelago");
        Log.Information($"Playing {Client.CurrentSession.ConnectionInfo.Game} as {Client.CurrentSession.Players.GetPlayerName(Client.CurrentSession.ConnectionInfo.Slot)}");
    }

    private static void OnDisconnected(object sender, EventArgs args)
    {
        Log.Information("Disconnected from Archipelago");
    }
}
