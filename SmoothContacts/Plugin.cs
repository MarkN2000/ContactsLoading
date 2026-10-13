using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using SkyFrost.Base;

namespace SmoothContacts;

[ResonitePlugin("markn2000.SmoothContacts", "SmoothContacts", "0.3.1", "markn2000", "https://github.com/MarkN2000/SmoothContacts")]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(BepisResoniteWrapper.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        Config.SaveOnConfigSet = false;
        ResoniteHooks.OnEngineReady += OnEngineReady;
    }

    private void OnEngineReady()
    {
        ResoniteHooks.OnEngineReady -= OnEngineReady;
        try
        {
            ContactsPatches.BindSettings(Config);
            if (!File.Exists(Config.ConfigFilePath)) Config.Save();
            ContactsPatches.ReportError = error => Log.LogError($"Contactsの分割生成を中止しました: {error}");
            ContactsPatches.Apply(HarmonyInstance);
            Log.LogInfo("SmoothContacts検証版: 最初からActive ONで分割生成します。最後の全体ソートは維持します。");
        }
        catch (Exception error)
        {
            HarmonyInstance.UnpatchSelf();
            Log.LogError($"SmoothContactsを適用できませんでした。本Modのパッチを解除しました: {error}");
        }
    }

    // 画面を生成途中で取り残さないよう、実行中のパッチ解除には対応しない。
    public override bool Unload() => false;
}

internal static class ContactsPatches
{
    // 検証版の切り替え。表示タイミングだけを変えて比較する。
    internal static readonly bool ActiveFromStart = true;
    internal static ConfigEntry<int> BatchSize = null!;
    internal static void BindSettings(ConfigFile config) => BatchSize = config.Bind("生成", "1更新あたりの件数", 10,
        "1更新で生成する最大件数。少ないほど負荷を分散し、多いほど生成が早く完了します。次の分割生成から反映します。1以上を指定してください（0以下は1として扱います）。");
    internal static Action<Exception>? ReportError;
    internal static readonly ConditionalWeakTable<ContactsDialog, Loading> Pending = new();
    internal static readonly AccessTools.FieldRef<ContactsDialog, Dictionary<string, ContactItem>> Items =
        AccessTools.FieldRefAccess<ContactsDialog, Dictionary<string, ContactItem>>("_contactItems");
    internal static readonly AccessTools.FieldRef<ContactsDialog, bool> SortRequested =
        AccessTools.FieldRefAccess<ContactsDialog, bool>("sortList");
    private static readonly AccessTools.FieldRef<ContactsDialog, SyncRef<TextField>> Search =
        AccessTools.FieldRefAccess<ContactsDialog, SyncRef<TextField>>("_searchBar");
    private static readonly AccessTools.FieldRef<ContactsDialog, SyncRef<Slot>> ListRoot =
        AccessTools.FieldRefAccess<ContactsDialog, SyncRef<Slot>>("_listRoot");
    private static readonly AccessTools.FieldRef<ContactsDialog, UIBuilder> ListUI =
        AccessTools.FieldRefAccess<ContactsDialog, UIBuilder>("listUi");
    private static readonly Func<ContactsDialog, ContactItem> AddItem =
        AccessTools.MethodDelegate<Func<ContactsDialog, ContactItem>>(AccessTools.Method(typeof(ContactsDialog), "AddContactItem"));

    internal static void Apply(Harmony harmony)
    {
        MethodInfo Target(string name, params Type[] parameters) =>
            AccessTools.DeclaredMethod(typeof(ContactsDialog), name, parameters)
            ?? throw new MissingMethodException(typeof(ContactsDialog).FullName, name);
        HarmonyMethod Patch(string name) => new(typeof(ContactsPatches), name);

        var attach = Target("OnAttach");
        var commonUpdate = Target("OnCommonUpdate");
        var updateItem = Target("UpdateContactItem", typeof(ContactData));
        var openPage = Target("OpenContactPage", typeof(string));
        var selectId = Target("set_SelectedContactId", typeof(string));
        var dispose = Target("OnDispose");
        var search = Target("SearchTextChanged", typeof(TextEditor));
        // 適用前に、元のDLLの差し替え箇所も検査する。
        TranspileAttach(PatchProcessor.GetOriginalInstructions(attach)).ToList();
        TranspileSort(PatchProcessor.GetOriginalInstructions(commonUpdate)).ToList();
        TranspileItem(PatchProcessor.GetOriginalInstructions(updateItem)).ToList();
        harmony.Patch(attach, transpiler: Patch(nameof(TranspileAttach)));
        harmony.Patch(commonUpdate, transpiler: Patch(nameof(TranspileSort)));
        harmony.Patch(updateItem, transpiler: Patch(nameof(TranspileItem)), postfix: Patch(nameof(AfterItem)));
        harmony.Patch(search, postfix: Patch(nameof(AfterSearch)));
        harmony.Patch(openPage, prefix: Patch(nameof(BeforeSelect)));
        harmony.Patch(selectId, prefix: Patch(nameof(BeforeSelect)));
        harmony.Patch(dispose, prefix: Patch(nameof(OnDispose)));
    }

    internal static IEnumerable<CodeInstruction> TranspileAttach(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var enumerate = AccessTools.Method(typeof(ContactManager), nameof(ContactManager.ForeachContactData));
        var matches = code.Select((op, index) => (op, index)).Where(p => p.op.Calls(enumerate)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"初期列挙の呼び出しが一意ではありません: {matches.Length}");
        int i = matches[0].index;
        if (i < 3 || code[i - 3].opcode != OpCodes.Ldarg_0 || code[i - 2].opcode != OpCodes.Ldftn
            || code[i - 2].operand is not MethodInfo callback || callback.IsStatic
            || callback.DeclaringType != typeof(ContactsDialog) || code[i - 1].opcode != OpCodes.Newobj)
            throw new InvalidOperationException("初期生成のコールバック形式が変わっています。");
        // 命令を差し替え、分岐先ラベルと例外ブロックは保持する。
        code[i].opcode = OpCodes.Call;
        code[i].operand = AccessTools.Method(typeof(ContactsPatches), nameof(Start));
        return code;
    }

    internal static IEnumerable<CodeInstruction> TranspileSort(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var field = AccessTools.Field(typeof(ContactsDialog), "sortList");
        var matches = code.Where(op => op.opcode == OpCodes.Ldfld && Equals(op.operand, field)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"ソート判定が一意ではありません: {matches.Length}");
        matches[0].opcode = OpCodes.Call;
        matches[0].operand = AccessTools.Method(typeof(ContactsPatches), nameof(ShouldSort));
        return code;
    }

    internal static int Priority(int unread, OnlineStatus status) => unread > 0 ? 0
        : status is OnlineStatus.Sociable or OnlineStatus.Online or OnlineStatus.Away or OnlineStatus.Busy ? 1 : 2;

    internal static IEnumerable<CodeInstruction> TranspileItem(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var target = AccessTools.Method(typeof(ContactsDialog), "AddContactItem");
        var matches = code.Select((op, i) => (op, i)).Where(p => p.op.Calls(target)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("行の生成箇所が一意ではありません。");
        var (call, index) = matches[0];
        var data = new CodeInstruction(OpCodes.Ldarg_1);
        data.labels.AddRange(call.labels);
        data.blocks.AddRange(call.blocks);
        call.labels.Clear();
        call.blocks.Clear();
        call.opcode = OpCodes.Call;
        call.operand = AccessTools.Method(typeof(ContactsPatches), nameof(CreateItem));
        code.Insert(index, data);
        return code;
    }

    // 対象版の本体と同じ比較キー。事前処理では値を固定し、完成時には現在値を使う。
    internal static (int Unread, int Category, long MessageTime, string Name) SortKey(
        Contact contact, OnlineStatus status, bool unread)
    {
        int category = contact.IsPartiallyMigrated ? 10 : contact.ContactStatus == ContactStatus.Requested ? 2
            : status switch { OnlineStatus.Sociable => 0, OnlineStatus.Online => 1,
                OnlineStatus.Away => 3, OnlineStatus.Busy => 4, _ => 5 };
        return (unread ? 0 : 1, category, -contact.LatestMessageTime.Ticks, contact.ContactUsername);
    }

    internal static (List<ContactData> Contacts, int PriorityEnd, Dictionary<string, int> Order) Prepare(
        List<ContactData> snapshot, Func<ContactData, int> unreadCount)
    {
        var sorted = snapshot.Select(data =>
        {
            var contact = data.Contact;
            var status = data.CurrentStatus?.OnlineStatus ?? OnlineStatus.Offline;
            int unread = unreadCount(data);
            return (Data: data, Group: Priority(unread, status), Key: SortKey(contact, status, unread > 0));
        }).OrderBy(entry => entry.Group).ThenBy(entry => entry.Key).ToArray();
        return (sorted.Select(entry => entry.Data).ToList(), sorted.Count(entry => entry.Group < 2),
            sorted.Select((entry, index) => (entry.Data.UserId, index)).ToDictionary(entry => entry.UserId, entry => entry.index));
    }

    private static void Start(ContactManager manager, Action<ContactData> update)
    {
        if (update.Target is not ContactsDialog dialog)
        {
            ReportError?.Invoke(new InvalidOperationException("初期生成の対象が想定外のため、本体の列挙を使用します。"));
            manager.ForeachContactData(update);
            return;
        }
        var state = new Loading(manager, update);
        Pending.Add(dialog, state);
        state.Tick = () => RunBatch(dialog, state);
        var messages = dialog.Cloud.Messages;
        dialog.StartTask(async () =>
        {
            Exception? failure = null;
            (List<ContactData> Contacts, int PriorityEnd, Dictionary<string, int> Order) prepared = ([], 0, []);
            try
            {
                await default(ToBackground);
                var snapshot = new List<ContactData>();
                manager.ForeachContactData(snapshot.Add);
                // Contacts側のロックを解放してから未読数を読む。UIには触らない。
                prepared = Prepare(snapshot, data => messages.GetUserMessages(data.UserId)?.UnreadCount ?? 0);
            }
            catch (Exception error) { failure = error; }
            await default(ToWorld);
            if (dialog.IsDisposed || dialog.IsDestroyed || !Pending.TryGetValue(dialog, out var current) || current != state) return;
            try
            {
                if (failure != null) throw failure;
                (state.Contacts, state.PriorityEnd, state.Order) = prepared;
                // 事前処理中に通常イベントで作られた行も、公開前に順序を確定する。
                foreach (var pair in state.Hidden)
                    if (!pair.Value.IsDestroyed) pair.Value.OrderOffset = state.RowOrder(pair.Key);
                dialog.RunInUpdates(1, state.Tick);
            }
            catch (Exception error) { Abort(dialog, state, error); }
        });
    }

    internal sealed class Loading(ContactManager manager, Action<ContactData> update)
    {
        internal readonly ContactManager Manager = manager;
        internal readonly Action<ContactData> Update = update;
        internal List<ContactData> Contacts = [];
        internal int PriorityEnd;
        internal Dictionary<string, int> Order = [];
        internal readonly Dictionary<string, Slot> Hidden = [];
        internal int Position;
        private long extraRows;
        internal Action Tick = null!;

        internal long RowOrder(string id) => (Order.TryGetValue(id, out int index) ? index : Contacts.Count + extraRows++) * 100L;

        internal (int Start, int End) TakeBatch()
        {
            int start = Position;
            int limit = Position < PriorityEnd ? PriorityEnd : Contacts.Count;
            Position += Math.Min(Math.Max(1, BatchSize.Value), limit - Position);
            return (start, Position);
        }
    }

    private static void RunBatch(ContactsDialog dialog, Loading state)
    {
        if (!Pending.TryGetValue(dialog, out var current) || current != state) return;
        try
        {
            var (start, end) = state.TakeBatch();
            for (int i = start; i < end; i++) Generate(dialog, state, state.Contacts[i]);
            if (state.Position == state.Contacts.Count) Finish(dialog, state);
            else
            {
                if (start < state.PriorityEnd && end == state.PriorityEnd)
                    for (int i = 0; i < state.PriorityEnd; i++) Publish(dialog, state, state.Contacts[i].UserId);
                dialog.RunInUpdates(1, state.Tick);
            }
        }
        catch (Exception error)
        {
            Abort(dialog, state, error);
        }
    }

    internal static void Generate(ContactsDialog dialog, Loading state, ContactData data)
    {
        if (Items(dialog).ContainsKey(data.UserId)) return;
        if (!ReferenceEquals(state.Manager.GetContact(data.UserId), data.Contact)) return;
        state.Update(data);
    }

    internal static void BeforeSelect(ContactsDialog __instance, string __0)
    {
        if (string.IsNullOrEmpty(__0) || !Pending.TryGetValue(__instance, out var state)) return;
        try
        {
            if (!Items(__instance).ContainsKey(__0))
            {
                ContactData? data = null;
                if (state.Order.TryGetValue(__0, out int index)) data = state.Contacts[index];
                // ponytail: 事前処理中など索引にない選択だけO(N)。通常の選択には索引を使う。
                else state.Manager.ForeachContactData(candidate => { if (candidate.UserId == __0) data = candidate; });
                if (data != null) Generate(__instance, state, data);
            }
            Publish(__instance, state, __0);
        }
        catch (Exception error)
        {
            Abort(__instance, state, error);
            throw;
        }
    }

    private static ContactItem CreateItem(ContactsDialog dialog, ContactData data)
    {
        if (!Pending.TryGetValue(dialog, out var state)) return AddItem(dialog);
        var ui = ListUI(dialog);
        var slot = ui.Root.AddSlot("Panel");
        slot.ActiveSelf = ActiveFromStart;
        slot.OrderOffset = state.RowOrder(data.UserId);
        state.Hidden[data.UserId] = slot;
        try
        {
            ui.ForceNext = slot.GetComponentOrAttach<RectTransform>();
            return AddItem(dialog);
        }
        catch
        {
            ui.ForceNext = null!;
            state.Hidden.Remove(data.UserId);
            slot.Destroy();
            throw;
        }
    }

    private static void AfterItem(ContactsDialog __instance, ContactData __0)
    {
        if (Pending.TryGetValue(__instance, out var state) && (ActiveFromStart || !state.Hidden.ContainsKey(__0.UserId))
            && Items(__instance).TryGetValue(__0.UserId, out var item))
            item.Slot.ActiveSelf = MatchesSearch(item.Username, item.AlternateNames, Search(__instance).Target.TargetString);
        // ponytail: 通常イベントの生成件数は制限しない。大量発生が実測で問題なら分割処理へ合流する。
    }

    private static void AfterSearch(ContactsDialog __instance)
    {
        if (!ActiveFromStart && Pending.TryGetValue(__instance, out var state))
            foreach (var slot in state.Hidden.Values)
                if (!slot.IsDestroyed) slot.ActiveSelf = false;
    }

    private static void Publish(ContactsDialog dialog, Loading state, string id)
    {
        if (state.Hidden.Remove(id, out var slot) && !ActiveFromStart && !slot.IsDestroyed
            && Items(dialog).TryGetValue(id, out var item) && item.Slot == slot)
            slot.ActiveSelf = MatchesSearch(item.Username, item.AlternateNames, Search(dialog).Target.TargetString);
    }

    internal static bool MatchesSearch(string username, IEnumerable<string> alternateNames, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string term = query.Trim().ToLower();
        if (username.StartsWith(term, StringComparison.InvariantCultureIgnoreCase)) return true;
        foreach (string name in alternateNames)
            if (name.StartsWith(term, StringComparison.InvariantCulture)) return true;
        return false;
    }

    internal static bool ShouldSort(ContactsDialog dialog) => SortRequested(dialog) && !Pending.TryGetValue(dialog, out _);

    internal static int CompareRows(Slot a, Slot b)
    {
        var first = a.GetComponent<ContactItem>();
        var second = b.GetComponent<ContactItem>();
        if (first?.Contact == null) return second?.Contact == null ? 0 : 1;
        if (second?.Contact == null) return -1;
        return SortKey(first.Contact, first.Data?.CurrentStatus?.OnlineStatus ?? OnlineStatus.Offline, first.HasMessages)
            .CompareTo(SortKey(second.Contact, second.Data?.CurrentStatus?.OnlineStatus ?? OnlineStatus.Offline, second.HasMessages));
    }

    private static void Finish(ContactsDialog dialog, Loading state)
    {
        ListRoot(dialog).Target.SortChildren(CompareRows);
        SortRequested(dialog) = false;
        Release(dialog, state);
    }

    private static void Release(ContactsDialog dialog, Loading state)
    {
        try
        {
            foreach (string id in state.Hidden.Keys.ToArray()) Publish(dialog, state, id);
        }
        finally { Pending.Remove(dialog); }
    }

    private static void Abort(ContactsDialog dialog, Loading state, Exception error)
    {
        try { Release(dialog, state); }
        finally
        {
            SortRequested(dialog) = true;
            ReportError?.Invoke(error);
        }
    }

    internal static void OnDispose(ContactsDialog __instance) => Pending.Remove(__instance);
}
