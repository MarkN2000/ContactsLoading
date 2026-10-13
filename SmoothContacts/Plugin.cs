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

[ResonitePlugin("markn2000.SmoothContacts", "SmoothContacts", "0.3.8", "markn2000", "https://github.com/MarkN2000/SmoothContacts")]
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
            ContactsPatches.ReportError = error => Log.LogError($"Contactsの分割処理でエラー: {error}");
            ContactsPatches.Apply(HarmonyInstance);
            Log.LogInfo("SmoothContacts: アクティブ中だけ、生成と検索解除の再表示を分割します。");
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
    internal static ConfigEntry<int> BatchSize = null!;
    internal static void BindSettings(ConfigFile config) => BatchSize = config.Bind("生成", "1更新あたりの件数", 10,
        "1更新で生成または検索解除の再表示を行う合計件数。通常の検索は即時反映します。次の更新から反映します。1以上を指定してください（0以下は1として扱います）。");
    internal static Action<Exception>? ReportError;
    internal static readonly ConditionalWeakTable<ContactsDialog, Loading> Pending = new();
    internal static readonly ConditionalWeakTable<ContactsDialog, PriorityQueue<ContactItem, long>> Restoring = new();
    internal static readonly AccessTools.FieldRef<ContactsDialog, Dictionary<string, ContactItem>> Items =
        AccessTools.FieldRefAccess<ContactsDialog, Dictionary<string, ContactItem>>("_contactItems");
    private static readonly AccessTools.FieldRef<ContactsDialog, List<ContactItem>> SearchResults =
        AccessTools.FieldRefAccess<ContactsDialog, List<ContactItem>>("_searchResultItems");
    internal static readonly AccessTools.FieldRef<ContactsDialog, bool> SortRequested =
        AccessTools.FieldRefAccess<ContactsDialog, bool>("sortList");
    private static readonly AccessTools.FieldRef<ContactsDialog, SyncRef<TextField>> Search =
        AccessTools.FieldRefAccess<ContactsDialog, SyncRef<TextField>>("_searchBar");
    private static readonly AccessTools.FieldRef<ContactsDialog, UIBuilder> ListUI =
        AccessTools.FieldRefAccess<ContactsDialog, UIBuilder>("listUi");

    internal static void Apply(Harmony harmony)
    {
        MethodInfo Target(string name, params Type[] parameters) =>
            AccessTools.DeclaredMethod(typeof(ContactsDialog), name, parameters)
            ?? throw new MissingMethodException(typeof(ContactsDialog).FullName, name);
        HarmonyMethod Patch(string name) => new(typeof(ContactsPatches), name);

        var attach = Target("OnAttach");
        var commonUpdate = Target("OnCommonUpdate");
        var activated = Target("OnActivated");
        var updateItem = Target("UpdateContactItem", typeof(ContactData));
        var openPage = Target("OpenContactPage", typeof(string));
        var selectId = Target("set_SelectedContactId", typeof(string));
        var search = Target("SearchTextChanged", typeof(TextEditor));
        var dispose = Target("OnDispose");
        // 適用前に、元のDLLの差し替え箇所も検査する。
        TranspileAttach(PatchProcessor.GetOriginalInstructions(attach)).ToList();
        TranspileSort(PatchProcessor.GetOriginalInstructions(commonUpdate), commonUpdate).ToList();
        TranspileSort(PatchProcessor.GetOriginalInstructions(activated), activated).ToList();
        TranspileSort(PatchProcessor.GetOriginalInstructions(updateItem), updateItem).ToList();
        TranspileSearch(PatchProcessor.GetOriginalInstructions(search)).ToList();
        harmony.Patch(attach, transpiler: Patch(nameof(TranspileAttach)));
        harmony.Patch(commonUpdate, transpiler: Patch(nameof(TranspileSort)));
        harmony.Patch(activated, transpiler: Patch(nameof(TranspileSort)));
        harmony.Patch(updateItem, prefix: Patch(nameof(BeforeItem)), transpiler: Patch(nameof(TranspileSort)));
        harmony.Patch(openPage, prefix: Patch(nameof(BeforeSelect)));
        harmony.Patch(selectId, prefix: Patch(nameof(BeforeSelect)));
        harmony.Patch(search, prefix: Patch(nameof(BeforeSearch)), transpiler: Patch(nameof(TranspileSearch)));
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

    internal static IEnumerable<CodeInstruction> TranspileSort(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var field = AccessTools.Field(typeof(ContactsDialog), "sortList");
        bool writing = __originalMethod.Name != "OnCommonUpdate";
        var matches = code.Where(op => op.opcode == (writing ? OpCodes.Stfld : OpCodes.Ldfld) && Equals(op.operand, field)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"ソート判定が一意ではありません: {matches.Length}");
        matches[0].opcode = OpCodes.Call;
        matches[0].operand = AccessTools.Method(typeof(ContactsPatches), !writing ? nameof(UpdateLoading)
            : __originalMethod.Name == "OnActivated" ? nameof(RequestActivationSort) : nameof(RequestItemSort));
        return code;
    }

    internal static IEnumerable<CodeInstruction> TranspileSearch(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var target = AccessTools.PropertySetter(typeof(Slot), nameof(Slot.ActiveSelf));
        var matches = code.Select((op, i) => (op, i)).Where(p => p.op.Calls(target)).ToArray();
        if (matches.Length != 2) throw new InvalidOperationException("検索の表示切替箇所が変わっています。");
        var (call, index) = matches[0];
        if (index == 0 || code[index - 1].opcode != OpCodes.Ldc_I4_1)
            throw new InvalidOperationException("検索解除のON指定が変わっています。");
        // 空欄の分岐だけ、trueの代わりにdialogを渡す。検索の一致判定側は本体のまま。
        code[index - 1].opcode = OpCodes.Ldarg_0;
        call.opcode = OpCodes.Call;
        call.operand = AccessTools.Method(typeof(ContactsPatches), nameof(QueueRestore));
        return code;
    }

    // 対象版の本体と同じ比較キー。事前処理で値を固定する。
    internal static (int Unread, int Category, long MessageTime, string Name) SortKey(
        Contact contact, OnlineStatus status, bool unread)
    {
        int category = contact.IsPartiallyMigrated ? 10 : contact.ContactStatus == ContactStatus.Requested ? 2
            : status switch { OnlineStatus.Sociable => 0, OnlineStatus.Online => 1,
                OnlineStatus.Away => 3, OnlineStatus.Busy => 4, _ => 5 };
        return (unread ? 0 : 1, category, -contact.LatestMessageTime.Ticks, contact.ContactUsername);
    }

    internal static (List<ContactData> Contacts, Dictionary<string, int> Order) Prepare(
        List<ContactData> snapshot, Func<ContactData, int> unreadCount)
    {
        var sorted = snapshot.Select(data =>
        {
            var contact = data.Contact;
            var status = data.CurrentStatus?.OnlineStatus ?? OnlineStatus.Offline;
            int unread = unreadCount(data);
            return (Data: data, Key: SortKey(contact, status, unread > 0));
        }).OrderBy(entry => entry.Key).ToArray();
        return (sorted.Select(entry => entry.Data).ToList(),
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
        SortRequested(dialog) = false;
        var messages = dialog.Cloud.Messages;
        dialog.StartTask(async () =>
        {
            Exception? failure = null;
            (List<ContactData> Contacts, Dictionary<string, int> Order) prepared = ([], []);
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
                (state.Contacts, state.Order) = prepared;
            }
            catch (Exception error) { Abort(dialog, error); }
        });
    }

    internal sealed class Loading(ContactManager manager, Action<ContactData> update)
    {
        internal readonly ContactManager Manager = manager;
        internal readonly Action<ContactData> Update = update;
        internal List<ContactData>? Contacts;
        internal Dictionary<string, int> Order = [];
        internal readonly Dictionary<string, ContactData> Deferred = [];
        internal int Position;
        internal bool Creating;
        internal string? Selection;
        internal Slot? Spacer;
        // -1は未集計、0は終了。検索解除後も再開しない。
        internal int ReservedRows = -1;
        private long extraRows;
        internal long RowOrder(string id) => (Order.TryGetValue(id, out int index) ? index : Contacts!.Count + extraRows++) * 100L;

        internal (int Start, int End) TakeBatch(int remaining)
        {
            int start = Position;
            Position += Math.Min(remaining, Contacts!.Count - Position);
            return (start, Position);
        }

        internal void ClearSpacer()
        {
            ReservedRows = 0;
            var slot = Spacer;
            Spacer = null;
            if (slot != null && !slot.IsRemoved) slot.Destroy();
        }
    }

    private static void UpdateSpacer(ContactsDialog dialog, Loading state)
    {
        if (state.ReservedRows == 0) return;
        if (state.ReservedRows < 0) state.ReservedRows = state.Contacts!.Count(data => !data.Contact.ShouldBeHidden);
        int remaining = state.ReservedRows - Items(dialog).Count;
        var root = ListUI(dialog).Root;
        var layout = root.GetComponent<VerticalLayout>();
        if (remaining <= 0 || layout == null)
        {
            state.ClearSpacer();
            return;
        }
        if (state.Spacer == null)
        {
            state.Spacer = root.AddSlot("SmoothContacts Spacer");
            state.Spacer.OrderOffset = long.MaxValue;
            state.Spacer.AttachComponent<RectTransform>();
            state.Spacer.AttachComponent<LayoutElement>();
        }
        // ponytail: 標準行高32と初期件数で予約する。可変行高・途中の件数変化には実測と再集計が必要。
        float spacing = layout.Spacing.Value;
        float height = remaining * (32f + spacing) - spacing;
        var element = state.Spacer.GetComponent<LayoutElement>();
        element.MinHeight.Value = element.PreferredHeight.Value = height;
    }

    // Activeの判定は本体に任せ、検索解除の再表示と生成で件数制限を共有する。
    internal static bool UpdateLoading(ContactsDialog dialog)
    {
        bool loading = Pending.TryGetValue(dialog, out var state);
        bool restoring = Restoring.TryGetValue(dialog, out var changes);
        if (!loading && !restoring) return SortRequested(dialog);
        int remaining = Math.Max(1, BatchSize.Value);
        if (restoring)
        {
            while (remaining > 0 && changes!.TryDequeue(out var item, out _))
            {
                remaining--;
                try
                {
                    if (!item.IsRemoved && !item.Slot.ActiveSelf) item.Slot.ActiveSelf = true;
                }
                catch (Exception error) { ReportError?.Invoke(error); }
            }
            if (changes!.Count == 0) Restoring.Remove(dialog);
        }
        if (!loading) return SortRequested(dialog);
        if (state!.Contacts == null) return false;
        try
        {
            string term = NormalizeQuery(Search(dialog).Target.TargetString);
            if (state.Selection is { } selected)
            {
                state.Selection = null;
                dialog.OpenContactPage(selected);
            }
            if (state.Position == state.Contacts.Count)
            {
                foreach (var pair in state.Deferred)
                {
                    state.Order[pair.Key] = state.Contacts.Count;
                    state.Contacts.Add(pair.Value);
                }
                state.Deferred.Clear();
            }
            var (start, end) = state.TakeBatch(remaining);
            for (int i = start; i < end; i++) Generate(dialog, state, state.Contacts[i], term);
            if (state.Position == state.Contacts.Count && state.Deferred.Count == 0) EndLoading(dialog);
            else UpdateSpacer(dialog, state);
        }
        catch (Exception error)
        {
            Abort(dialog, error);
        }
        return false;
    }

    internal static void Generate(ContactsDialog dialog, Loading state, ContactData data, string term)
    {
        if (state.Deferred.Remove(data.UserId, out var latest)) data = latest;
        if (Items(dialog).ContainsKey(data.UserId)) return;
        if (!ReferenceEquals(state.Manager.GetContact(data.UserId), data.Contact)) return;
        state.Creating = true;
        try
        {
            state.Update(data);
            if (Items(dialog).TryGetValue(data.UserId, out var item))
            {
                item.Slot.OrderOffset = state.RowOrder(data.UserId);
                if (term.Length > 0 && !MatchesSearch(item.Username, item.AlternateNames, term)) item.Slot.ActiveSelf = false;
            }
        }
        finally { state.Creating = false; }
    }

    internal static bool BeforeSelect(ContactsDialog __instance, string __0)
    {
        if (string.IsNullOrEmpty(__0) || !Pending.TryGetValue(__instance, out var state)) return true;
        state.Selection = null;
        if (Items(__instance).ContainsKey(__0) || SearchResults(__instance).Any(item => item.Contact.ContactUserId == __0)) return true;
        if (state.Contacts == null || !__instance.Slot.IsActive)
        {
            state.Selection = __0;
            return false;
        }
        try
        {
            ContactData? data = null;
            if (state.Order.TryGetValue(__0, out int index)) data = state.Contacts[index];
            // ponytail: 索引にない選択だけO(N)。通常の選択には索引を使う。
            else state.Manager.ForeachContactData(candidate => { if (candidate.UserId == __0) data = candidate; });
            if (data != null) Generate(__instance, state, data, NormalizeQuery(Search(__instance).Target.TargetString));
            if (Items(__instance).ContainsKey(__0)) UpdateSpacer(__instance, state);
            else state.ClearSpacer();
        }
        catch (Exception error)
        {
            Abort(__instance, error);
            throw;
        }
        return true;
    }

    internal static bool BeforeItem(ContactsDialog __instance, ContactData __0)
    {
        if (Pending.TryGetValue(__instance, out var state) && !state.Creating && !Items(__instance).ContainsKey(__0.UserId))
        {
            state.Deferred[__0.UserId] = __0;
            SortRequested(__instance) = true;
            return false;
        }
        return true;
    }

    internal static void BeforeSearch(ContactsDialog __instance)
    {
        Restoring.Remove(__instance);
        if (Pending.TryGetValue(__instance, out var state)) state.ClearSpacer();
    }

    internal static void QueueRestore(Slot slot, ContactsDialog dialog)
    {
        if (!slot.ActiveSelf)
            Restoring.GetValue(dialog, _ => new()).Enqueue(slot.GetComponent<ContactItem>(), slot.OrderOffset);
    }

    internal static string NormalizeQuery(string? query) => query?.Trim().ToLower() ?? "";

    internal static bool MatchesSearch(string username, IEnumerable<string> alternateNames, string term)
    {
        if (term.Length == 0) return true;
        if (username.StartsWith(term, StringComparison.InvariantCultureIgnoreCase)) return true;
        foreach (string name in alternateNames)
            if (name.StartsWith(term, StringComparison.InvariantCulture)) return true;
        return false;
    }

    internal static void RequestActivationSort(ContactsDialog dialog, bool requested)
    {
        if (!Pending.TryGetValue(dialog, out _)) SortRequested(dialog) = requested;
    }

    internal static void RequestItemSort(ContactsDialog dialog, bool requested)
    {
        if (!Pending.TryGetValue(dialog, out var state) || !state.Creating) SortRequested(dialog) = requested;
    }

    private static void Abort(ContactsDialog dialog, Exception error)
    {
        EndLoading(dialog);
        SortRequested(dialog) = true;
        ReportError?.Invoke(error);
    }

    private static void EndLoading(ContactsDialog dialog)
    {
        if (Pending.TryGetValue(dialog, out var state)) state.ClearSpacer();
        Pending.Remove(dialog);
    }

    internal static void OnDispose(ContactsDialog __instance)
    {
        EndLoading(__instance);
        Restoring.Remove(__instance);
    }
}
