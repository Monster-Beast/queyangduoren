using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // Runs only in the QA driver's private copy. The fixture adds one loose stack once.
    // Real constructables, MaterialNeeds, duplicant fetch chores and building completion do the rest.
    internal sealed class MaterialCompetition
    {
        enum Phase { Setup, WaitSupply, WaitReservations, WaitRefund, Delivery, Settled }
        static MaterialCompetition observing;
        readonly Action<string> log;
        readonly WorldContainer world = ClusterManager.Instance.activeWorld;
        readonly BuildingDef ladder = Assets.GetBuildingDef(LadderConfig.ID);
        readonly float started = Time.realtimeSinceStartup;
        Phase phase;
        Element element;
        float requirement, nextReport, entered;
        int a, b, source, carrierId = -1, deliveryOrder, suppliedPile;
        MinionIdentity hauler;
        bool secondAccepted, sawDelivery;

        public MaterialCompetition(Action<string> record) { log = record; }

        public bool Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - started > 250) throw new TimeoutException("Material competition timed out in " + phase + " " + Ledger());
            switch (phase)
            {
                case Phase.Setup:
                    if (!global::Game.Instance.GameStarted() || Time.realtimeSinceStartup - started < 2) return false;
                    if (!FindSites()) return false;
                    requirement = ladder.Mass[0];
                    ChooseMaterial();
                    var resource = element.substance.SpawnResource(Grid.CellToPosCCC(source, Grid.SceneLayer.Ore),
                        requirement, 293.15f, 255, 0, true);
                    Expect(resource != null, "Native resource spawn failed.");
                    suppliedPile = resource.GetComponent<KPrefabID>().InstanceID;
                    DiscoveredResources.Instance.Discover(element.tag);
                    log(FormattableString.Invariant($"MATERIAL_INPUT prefab={element.tag} kg={requirement} pile={suppliedPile} source={source} A={a} B={b}"));
                    SpeedControlScreen.Instance.Unpause(false);
                    Move(Phase.WaitSupply); break;
                case Phase.WaitSupply:
                    if (Available() + .01f < requirement) return false;
                    Close(Physical(out _, out _, out _), requirement, "initial physically present stock");
                    Close(Need(), 0, "initial native reservation");
                    SpeedControlScreen.Instance.Pause(false);
                    var first = Place(a);
                    Expect(first.StartsWith("OK ", StringComparison.Ordinal), "First blueprint refused: " + first);
                    var second = Place(b);
                    secondAccepted = second.StartsWith("OK ", StringComparison.Ordinal);
                    Expect(secondAccepted || second.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0,
                        "Second blueprint failed for a non-material reason: " + second);
                    log("MATERIAL_COMPETE first=" + first + " second=" + second);
                    Move(Phase.WaitReservations); break;
                case Phase.WaitReservations:
                    if (Order(a) == null || (secondAccepted && Order(b) == null) || Need() < requirement - .01f) return false;
                    // MaterialNeeds is demand; two blueprints may legitimately request twice the available mass.
                    float submittedNeed = requirement * (secondAccepted ? 2 : 1);
                    Close(Need(), submittedNeed, "registered native blueprint demand");
                    Close(Available(), 0, "available material after native reservations");
                    Close(Physical(out _, out _, out _), requirement, "blueprint registration duplicated or consumed stock");
                    string before = Ledger();
                    Cancel(a);
                    if (secondAccepted) Cancel(b);
                    log("MATERIAL_RESERVED " + before);
                    Move(Phase.WaitRefund); break;
                case Phase.WaitRefund:
                    if (Order(a) != null || Order(b) != null || Need() > .01f) return false;
                    SpeedControlScreen.Instance.Unpause(false);
                    if (Available() < requirement - .01f) return false;
                    Close(Physical(out _, out _, out _), requirement, "cancellation refund");
                    log("MATERIAL_REFUND " + Ledger());
                    var retry = Place(b);
                    Expect(retry.StartsWith("OK ", StringComparison.Ordinal), "Retry failed after cancellation: " + retry);
                    var consumer = hauler.GetComponent<ChoreConsumer>();
                    foreach (var group in new[] { Db.Get().ChoreGroups.Build, Db.Get().ChoreGroups.Hauling, Db.Get().ChoreGroups.Storage })
                        consumer.SetPersonalPriority(group, 5);
                    observing = this;
                    Move(Phase.Delivery); break;
                case Phase.Delivery:
                    if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                    var order = Order(b);
                    if (order != null && deliveryOrder == 0)
                    {
                        deliveryOrder = order.GetComponent<KPrefabID>().InstanceID;
                        order.GetComponent<Prioritizable>()?.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 9));
                    }
                    if (now >= nextReport) { nextReport = now + 10; log("MATERIAL_PROGRESS " + Ledger() + WorkerState()); }
                    if (Finished(b) == null) return false;
                    Move(Phase.Settled); break;
                case Phase.Settled:
                    if (now - entered < 1 || Need() > .01f) return false;
                    var complete = Finished(b); Expect(complete != null && Finished(a) == null, "Expected exactly one completed ladder.");
                    var primary = complete.GetComponent<PrimaryElement>();
                    Expect(primary != null && primary.ElementID == element.id, "The final ladder used a different material.");
                    float remaining = Physical(out _, out _, out _);
                    Close(primary.Mass, requirement, "material in finished ladder");
                    Close(remaining + primary.Mass, requirement, "final physical mass balance");
                    Close(Need(), 0, "unreleased demand");
                    Expect(carrierId >= 0 && sawDelivery, "No native pickup/blueprint storage transfer observed: " + Ledger());
                    observing = null;
                    log(FormattableString.Invariant($"PASS material competition sourceKg={requirement} finishedKg={primary.Mass} remainingKg={remaining} carrier={carrierId} order={deliveryOrder}"));
                    return true;
            }
            return false;
        }

        bool FindSites()
        {
            foreach (var worker in Components.LiveMinionIdentities.Items)
            {
                var nav = worker == null ? null : worker.GetComponent<Navigator>();
                if (nav == null) continue;
                source = Grid.PosToCell(worker);
                if (!Ground(source) || Grid.WorldIdx[source] != world.id) continue;
                Grid.CellToXY(source, out int x, out int y);
                var sites = new List<int>();
                for (int radius = 3; radius <= 14; radius++)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            int xx = x + sign * radius, yy = y + dy;
                            if (xx < 0 || xx >= Grid.WidthInCells || yy < 1 || yy >= Grid.HeightInCells) continue;
                            int cell = Grid.XYToCell(xx, yy);
                            if (Grid.WorldIdx[cell] != world.id || !Ground(cell) || !nav.CanReach(cell) ||
                                Grid.Objects[cell, (int)ladder.ObjectLayer] != null ||
                                !ladder.IsValidPlaceLocation(ladder.BuildingPreview, cell, Orientation.Neutral, out _)) continue;
                            sites.Add(cell);
                            if (sites.Count == 2) { a = sites[0]; b = sites[1]; hauler = worker; return true; }
                        }
            }
            return false;
        }

        static bool Ground(int cell)
        {
            if (!Grid.IsValidCell(cell) || !Grid.IsValidCell(Grid.CellBelow(cell)) || Grid.Visible[cell] == 0) return false;
            return !Grid.Solid[cell] && Grid.Solid[Grid.CellBelow(cell)];
        }

        void ChooseMaterial()
        {
            var existing = new HashSet<Tag>();
            foreach (var pickup in Components.Pickupables.Items)
                if (pickup != null && pickup.KPrefabID != null) existing.Add(pickup.KPrefabID.PrefabTag);
            var valid = MaterialSelector.GetValidMaterials(ladder.MaterialCategory[0], true);
            var preferred = new[] { "Granite", "IgneousRock", "SedimentaryRock", "SandStone", "MaficRock", "Obsidian", "Fossil" };
            var candidates = new List<Tag>();
            foreach (var name in preferred) { var tag = new Tag(name); if (valid.Contains(tag)) candidates.Add(tag); }
            candidates.AddRange(valid);
            foreach (var tag in candidates)
            {
                var candidate = ElementLoader.FindElementByTag(tag);
                if (candidate == null || !candidate.IsSolid || candidate.lowTemp > 270 || candidate.highTemp < 400 ||
                    existing.Contains(tag) || world.worldInventory.GetTotalAmount(tag, false) > .01f ||
                    MaterialNeeds.GetAmount(tag, world.id, false) > .01f) continue;
                element = candidate; return;
            }
            throw new InvalidOperationException("No unused, thermally stable native ladder material in this fixture.");
        }

        string Place(int cell)
        {
            Grid.CellToXY(cell, out int x, out int y);
            return HostWorld.Execute("build/ladder/0/" + element.tag, x, y);
        }

        void Cancel(int cell)
        {
            Grid.CellToXY(cell, out int x, out int y);
            var view = ColonyView.Decode(HostWorld.Capture(x, y));
            var target = view.Cells[(y - view.Y) * ColonyView.Width + x - view.X].TargetId;
            var result = HostWorld.Execute("cancel:" + target.ToString(CultureInfo.InvariantCulture), x, y);
            Expect(result.StartsWith("OK ", StringComparison.Ordinal), "Cancel rejected: " + result);
        }

        Constructable Order(int cell) => Grid.Objects[cell, (int)ladder.ObjectLayer]?.GetComponent<Constructable>();
        BuildingComplete Finished(int cell) => Grid.Objects[cell, (int)ladder.ObjectLayer]?.GetComponent<BuildingComplete>();
        float Need() => MaterialNeeds.GetAmount(element.tag, world.id, false);
        float Available() => world.worldInventory.GetAmount(element.tag, false);

        float Physical(out float floor, out float carried, out float stored)
        {
            floor = carried = stored = 0;
            foreach (var p in Components.Pickupables.Items)
            {
                if (p == null || p.KPrefabID == null || p.KPrefabID.PrefabTag != element.tag) continue;
                var m = p.PrimaryElement.Mass;
                if (p.storage == null) floor += m;
                else if (p.storage.GetComponentInParent<MinionIdentity>() != null) carried += m;
                else stored += m;
            }
            return floor + carried + stored;
        }

        string Ledger()
        {
            if (element == null) return "waiting for accessible, legal sites";
            float total = Physical(out float floor, out float carried, out float stored);
            return FormattableString.Invariant($"demand={Need()} available={Available()} ground={floor} carried={carried} inStorage={stored} physical={total}");
        }

        string WorkerState()
        {
            var work = hauler.GetComponent<ChoreDriver>()?.GetCurrentChore();
            var supply = KPrefabIDTracker.Get().GetInstance(suppliedPile)?.GetComponent<Pickupable>();
            var nav = hauler.GetComponent<Navigator>();
            return FormattableString.Invariant($" sim={GameClock.Instance.GetTime():0.0} speed={Time.timeScale} dupe={hauler.GetProperName()} chore={work?.choreType.Id} at={Grid.PosToCell(hauler)} canReach={nav.CanReach(b)} sourceReach={nav.CanReach(source)} reservedStack={supply?.ReservedAmount}");
        }

        // Witness the native transport callback even when delivery and construction complete
        // between two driver frames. The fixture never assigns storage or invokes a fetch chore.
        [HarmonyPatch(typeof(Pickupable), "set_storage")]
        static class WitnessStorage
        {
            static void Postfix(Pickupable __instance, Storage __0)
            {
                var test = observing;
                if (test == null || __0 == null || __instance.KPrefabID == null || __instance.KPrefabID.PrefabTag != test.element.tag) return;
                var dupe = __0.GetComponentInParent<MinionIdentity>();
                if (dupe != null && test.carrierId < 0)
                {
                    test.carrierId = dupe.GetComponent<KPrefabID>().InstanceID;
                    test.log(FormattableString.Invariant($"MATERIAL_HAUL pile={__instance.KPrefabID.InstanceID} carrier={test.carrierId} kg={__instance.PrimaryElement.Mass}"));
                }
                if (__0.GetComponent<Constructable>() != null && Grid.PosToCell(__0) == test.b && !test.sawDelivery)
                {
                    test.sawDelivery = true;
                    test.log(FormattableString.Invariant($"MATERIAL_DELIVER pile={__instance.KPrefabID.InstanceID} site={test.b} kg={__instance.PrimaryElement.Mass}"));
                }
            }
        }

        void Move(Phase next) { phase = next; entered = Time.realtimeSinceStartup; }
        static void Expect(bool ok, string msg) { if (!ok) throw new InvalidOperationException(msg); }
        static void Close(float value, float target, string label) =>
            Expect(Math.Abs(value-target) <= .02f, FormattableString.Invariant($"{label}: got {value} expected {target}"));
    }
}
