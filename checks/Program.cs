using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Configuration;
using SmoothContacts;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using SkyFrost.Base;

internal static class Program
{
    private static int assertions;
    private static readonly Slot root = Empty<Slot>();
    private static readonly TextField search = Empty<TextField>();
    private static readonly Dictionary<Slot, ContactItem> rows = [];
    private static readonly Dictionary<Slot, bool> active = [];
    private static readonly Dictionary<SyncRef<Text>, Text> texts = [];
    private static readonly List<string> selections = [];
    private static string query = "";
    private static bool visible = true;
    private static int activeWrites;

    private static void Main(string[] args)
    {
        string game = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
        string profile = args.Length > 1 ? args[1] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"com.kesomannen.gale\resonite\profiles\Mod開発");
        // 元の場所からDLLを読み、検査プロセス内だけでパッチする。Engineは起動しない。
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? file = new[] { Path.Combine(profile, "BepInEx", "core"), game }
                .Select(dir => Path.Combine(dir, name.Name + ".dll")).FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(Path.Combine(profile, "BepInEx", "plugins"), name.Name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            return file == null ? null : context.LoadFromAssemblyPath(file);
        };
        Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        CheckSettings();
        var manager = Empty<ContactManager>();
        var current = new Dictionary<string, ContactData>();
        AccessTools.Field(typeof(ContactManager), "_lock").SetValue(manager, new object());
        AccessTools.Field(typeof(ContactManager), "contacts").SetValue(manager, current);
        ContactData Data(string id, OnlineStatus status = OnlineStatus.Offline)
        {
            var data = new ContactData(manager, new Contact { ContactUserId = id, ContactUsername = id });
            data.CurrentStatus.OnlineStatus = status;
            current[id] = data;
            return data;
        }

        foreach (int count in new[] { 0, 1, 9, 10, 11, 23, 32 })
        {
            var state = new ContactsPatches.Loading(manager, _ => { }) { Contacts = Enumerable.Repeat(Data("batch"), count).ToList() };
            var visited = new List<int>();
            while (state.Position < count)
            {
                var (start, end) = state.TakeBatch();
                Check(end > start && end - start <= 10, "1更新最大10件、必ず進む");
                visited.AddRange(Enumerable.Range(start, end - start));
            }
            Check(visited.SequenceEqual(Enumerable.Range(0, count)), "全件を1回ずつ走査");
        }
        Check(ContactsPatches.MatchesSearch("Alice", [], " AL "), "検索の前後空白・大小文字");
        Check(ContactsPatches.MatchesSearch("Alice", ["別名"], "別"), "別名の前方一致");
        Check(!ContactsPatches.MatchesSearch("Alice", [], "lic"), "部分一致には変えない");
        Check(ContactsPatches.MatchesSearch("Alice", [], "  "), "空の検索");

        foreach (string name in new[] { "OnAttach", "OnCommonUpdate", "OnActivated", "UpdateContactItem" })
        {
            var method = AccessTools.DeclaredMethod(typeof(ContactsDialog), name);
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            int length = original.Count;
            Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transform = name switch {
                "OnAttach" => ContactsPatches.TranspileAttach, "UpdateContactItem" => ContactsPatches.TranspileItem,
                _ => code => ContactsPatches.TranspileSort(code, method) };
            var changed = transform(original).ToList();
            Check(changed.Count == length + (name == "UpdateContactItem" ? 1 : 0), "ILの追加は必要な引数だけ");
            Check(changed.Count(op => op.operand is MethodInfo m && m.DeclaringType == typeof(ContactsPatches))
                == (name == "UpdateContactItem" ? 2 : 1), "生成とソート要求の必要箇所だけ差し替え");
            Reject(() => transform([]).ToList());
            var doubled = PatchProcessor.GetOriginalInstructions(method).ToList();
            Reject(() => transform(doubled.Concat(doubled)).ToList());
        }
        var create = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ContactsPatches), "CreateItem")).ToList();
        int setOrder = create.FindIndex(op => op.Calls(AccessTools.PropertySetter(typeof(Slot), "OrderOffset")));
        int attachRect = create.FindIndex(op => op.operand is MethodInfo m && m.Name == "GetComponentOrAttach" && m.GetGenericArguments().Contains(typeof(RectTransform)));
        Check(setOrder >= 0 && setOrder < attachRect, "RectTransformより先にOrderOffsetを設定");
        Check(!create.Any(op => op.Calls(AccessTools.PropertySetter(typeof(Slot), "ActiveSelf"))), "新規Slotの既定Active ONをそのまま使う");
        Check(!typeof(ContactsPatches).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SelectMany(method => PatchProcessor.GetOriginalInstructions(method))
            .Any(op => op.operand is MethodInfo m && m.Name is "SortChildren" or "RunInUpdates"), "全体ソートと更新予約を持たない");

        var boundary = new Harmony("SmoothContacts.Checks.Boundary");
        void Patch(MethodInfo target, string prefix) => boundary.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
        try
        {
            Patch(AccessTools.PropertyGetter(typeof(Slot), "IsActive"), nameof(IsActive));
            Patch(AccessTools.PropertySetter(typeof(Slot), "ActiveSelf"), nameof(SetActive));
            Patch(AccessTools.PropertyGetter(typeof(SyncRef<Text>), "Target"), nameof(GetText));
            Patch(AccessTools.PropertyGetter(typeof(TextField), "TargetString"), nameof(GetQuery));
            var getItem = typeof(ContainerWorker<Component>).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(ContactItem));
            Patch(getItem, nameof(GetItem));
            Patch(AccessTools.DeclaredMethod(typeof(ContactsDialog), "OpenContactPage"), nameof(OpenPage));
            CheckNativeOrder(manager, Data);
            // 参照型のジェネリックgetterは実行コードを共有するため、検査の段階ごとに置換する。
            boundary.Unpatch(AccessTools.PropertyGetter(typeof(SyncRef<Text>), "Target"), HarmonyPatchType.All, boundary.Id);
            Patch(AccessTools.PropertyGetter(typeof(SyncRef<TextField>), "Target"), nameof(GetSearch));
            CheckLoading(manager, current, Data);
        }
        finally { boundary.UnpatchSelf(); ContactsPatches.ReportError = null; }

        var harmony = new Harmony("SmoothContacts.Checks");
        try
        {
            ContactsPatches.Apply(harmony);
            foreach (string name in new[] { "OnAttach", "OnCommonUpdate", "OnActivated", "UpdateContactItem", "OpenContactPage", "set_SelectedContactId", "OnDispose" })
                Check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ContactsDialog), name))?.Owners.Contains(harmony.Id) == true,
                    $"実DLLへのパッチ適用: {name}");
        }
        finally { harmony.UnpatchSelf(); }
        Console.WriteLine($"PASS: {assertions}件の検査。ゲーム起動・ゲームファイル変更なし。");
    }

    private static void CheckSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SmoothContacts-check-{Guid.NewGuid():N}.cfg");
        try
        {
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            ContactsPatches.BindSettings(config);
            Check(config.Count == 1 && ContactsPatches.BatchSize.Value == 10, "設定は件数だけ、初期値10");
            var state = new ContactsPatches.Loading(Empty<ContactManager>(), _ => { }) {
                Contacts = Enumerable.Repeat<ContactData>(null!, 100).ToList() };
            Check(state.TakeBatch() == (0, 10), "初期値で10件ずつ進む");
            ContactsPatches.BatchSize.Value = 3;
            Check(state.TakeBatch() == (10, 13), "途中変更を次のバッチへ反映");
            config.Save();
            ContactsPatches.BindSettings(new ConfigFile(path, false) { SaveOnConfigSet = false });
            Check(ContactsPatches.BatchSize.Value == 3, "標準設定の保存と再読込");
            foreach (int value in new[] { 0, -1, int.MinValue })
            {
                ContactsPatches.BatchSize.Value = value;
                var (start, end) = state.TakeBatch();
                Check(end == start + 1, "0以下でも1件ずつ進む");
            }
            ContactsPatches.BatchSize.Value = int.MaxValue;
            Check(state.TakeBatch() == (16, 100), "大きな件数でも全件数を超えない");
            Check(state.TakeBatch() == (100, 100), "完了後は進まない");
        }
        finally { File.Delete(path); ContactsPatches.BatchSize.Value = 10; }
    }

    private static void CheckNativeOrder(ContactManager manager, Func<string, OnlineStatus, ContactData> data)
    {
        var input = new List<ContactData>();
        foreach (var status in Enum.GetValues<OnlineStatus>().Append((OnlineStatus)999))
        foreach (int category in new[] { 0, 1, 2 })
        {
            var entry = data($"order{input.Count:D2}", status);
            entry.Contact.LatestMessageTime = new DateTime(2026, 10, 13).AddHours(input.Count % 3);
            entry.Contact.ContactUsername = input.Count % 2 == 0 ? "Alice" : "Bob";
            if (category == 1) entry.Contact.ContactStatus = ContactStatus.Requested;
            if (category == 2) { entry.Contact.IsMigrated = true; entry.Contact.IsCounterpartMigrated = false; }
            input.Add(entry);
        }
        input.Reverse();
        var unread = input.Select((entry, index) => (entry.UserId, Count: index % 2)).ToDictionary(p => p.UserId, p => p.Count);
        var slots = input.Select(entry => Item(entry, unread[entry.UserId] > 0).Slot).ToList();
        var slotsById = slots.ToDictionary(slot => rows[slot].Data.UserId);
        var native = PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(ContactsDialog), "OnCommonUpdate"))
            .Where(op => op.opcode == OpCodes.Ldftn).Select(op => op.operand).OfType<MethodInfo>()
            .Single(m => m.ReturnType == typeof(int) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Slot), typeof(Slot) }));
        var compare = native.CreateDelegate<Comparison<Slot>>(native.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(native.DeclaringType!));
        slots.Sort(compare);
        int reads = 0;
        var prepared = ContactsPatches.Prepare(input, entry => { reads++; return unread[entry.UserId]; });
        Check(prepared.Contacts.Select((entry, index) => compare(slots[index], slotsById[entry.UserId]) == 0).All(v => v),
            "未読・全状態・受信リクエスト・移行未完了・日時・名前の順が実DLLの比較と一致");
        Check(reads == input.Count, "未読数は各1回だけ取得");
        Check(prepared.Contacts.Select((entry, index) => prepared.Order[entry.UserId] == index).All(v => v), "順位の索引が事前順と一致");
        var request = data("request", OnlineStatus.Offline);
        request.Contact.ContactStatus = ContactStatus.Requested;
        var away = data("away", OnlineStatus.Away);
        Check(ContactsPatches.Prepare([away, request], _ => 0).Contacts[0] == request, "独自のオンライン優先群を撤去し、受信リクエストをAwayより先にする");
        rows.Clear(); active.Clear(); texts.Clear();
    }

    private static void CheckLoading(ContactManager manager, Dictionary<string, ContactData> current, Func<string, OnlineStatus, ContactData> data)
    {
        var dialog = Empty<ContactsDialog>();
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(dialog, [root]);
        AccessTools.Field(typeof(ContactsDialog), "_searchBar").SetValue(dialog, new SyncRef<TextField>());
        ContactsPatches.Items(dialog) = [];
        ContactsPatches.ReportError = error => throw new InvalidOperationException("生成検査中の例外", error);
        var created = new List<string>();
        void Update(ContactData entry)
        {
            Check(ContactsPatches.BeforeItem(dialog, entry), "分割生成からの更新は本体へ通す");
            var item = Item(entry);
            ContactsPatches.Items(dialog)[entry.UserId] = item;
            created.Add(entry.UserId);
            if (entry.UserId == "nested") ContactsPatches.SortRequested(dialog) = true;
            ContactsPatches.RequestItemSort(dialog, true);
            AccessTools.Method(typeof(ContactsPatches), "AfterItem").Invoke(null, [dialog, entry]);
        }
        var state = new ContactsPatches.Loading(manager, Update);
        ContactsPatches.Pending.Add(dialog, state);
        Check(!ContactsPatches.UpdateLoading(dialog) && created.Count == 0, "事前処理中は生成しない");
        Check(!ContactsPatches.BeforeSelect(dialog, "row30") && state.Selection == "row30", "事前処理中の選択を保持");
        state.Selection = null;
        (state.Contacts, state.Order) = ContactsPatches.Prepare(Enumerable.Range(0, 35).Select(i => data($"row{i:D2}", OnlineStatus.Offline)).ToList(), _ => 0);
        ContactsPatches.SortRequested(dialog) = false;
        Check(!ContactsPatches.UpdateLoading(dialog) && created.Count == 10 && active.Values.All(v => v), "10件ずつ最初から表示");
        Check(!ContactsPatches.SortRequested(dialog) && activeWrites == 0, "初期生成のソート要求を除き、空の検索ではActiveを書き直さない");
        visible = false;
        ContactsPatches.RequestActivationSort(dialog, true);
        Check(!ContactsPatches.SortRequested(dialog), "生成中の再表示ソート要求を除く");
        for (int i = 0; i < 3; i++) Check(!ContactsPatches.UpdateLoading(dialog) && created.Count == 10 && state.Position == 10, "親が非アクティブなら位置を進めず生成しない");
        Check(!ContactsPatches.BeforeSelect(dialog, "row30"), "非アクティブ中の選択を保留");
        visible = true;
        query = "row2";
        foreach (var item in ContactsPatches.Items(dialog).Values) active[item.Slot] = ContactsPatches.MatchesSearch(item.Username, item.AlternateNames, query);
        ContactsPatches.UpdateLoading(dialog);
        Check(created.Count == 21 && selections.SequenceEqual(new[] { "row30" }) && state.Selection == null, "再表示で選択先1件と続き10件を生成");
        Check(!active[ContactsPatches.Items(dialog)["row30"].Slot], "選択先にも途中の検索条件を適用");
        while (ContactsPatches.Pending.TryGetValue(dialog, out _)) Check(!ContactsPatches.UpdateLoading(dialog), "完成した更新でも本体ソートを呼ばない");
        Check(created.Count == 35 && created.Distinct().Count() == 35 && active.Values.Count(v => v) == 10, "全件表示・重複なし・途中検索");
        Check(!ContactsPatches.UpdateLoading(dialog), "初期生成だけなら完成後もソート要求なし");
        ContactsPatches.RequestActivationSort(dialog, true);
        Check(ContactsPatches.UpdateLoading(dialog), "完成後の通常の再表示ソートは本体へ戻す");

        ContactsPatches.Items(dialog).Clear(); rows.Clear(); active.Clear(); texts.Clear(); created.Clear(); query = "";
        var deleted = data("deleted", OnlineStatus.Offline);
        var replaced = data("replaced", OnlineStatus.Offline);
        var normal = data("normal", OnlineStatus.Offline);
        var existing = data("existing", OnlineStatus.Offline);
        (var contacts, var order) = ContactsPatches.Prepare([deleted, replaced, normal, existing], _ => 0);
        var events = new ContactsPatches.Loading(manager, Update) { Contacts = contacts, Order = order };
        ContactsPatches.Pending.Add(dialog, events);
        ContactsPatches.Items(dialog)[existing.UserId] = Item(existing);
        current.Remove(deleted.UserId);
        data("replaced", OnlineStatus.Online);
        var extra = data("extra", OnlineStatus.Offline);
        Check(!ContactsPatches.BeforeItem(dialog, extra), "通常イベントの未生成行をキューへ合流");
        var latest = data("extra", OnlineStatus.Busy);
        Check(!ContactsPatches.BeforeItem(dialog, latest) && events.Deferred.Count == 1 && events.Deferred["extra"] == latest, "同じ相手の保留は最新データ1件だけ");
        Check(ContactsPatches.BeforeItem(dialog, existing), "既存行の通常更新は本体へ通す");
        while (ContactsPatches.Pending.TryGetValue(dialog, out _)) ContactsPatches.UpdateLoading(dialog);
        Check(created.SequenceEqual(new[] { "normal", "extra" }), "削除と旧データを除き、既存行を重複せず追加の相手も生成");
        Check(ContactsPatches.Items(dialog)["extra"].Data == latest && ContactsPatches.UpdateLoading(dialog), "最新データと通常イベントのソート要求を維持");

        ContactsPatches.SortRequested(dialog) = false;
        var nested = new ContactsPatches.Loading(manager, Update) { Contacts = [data("nested", OnlineStatus.Offline)] };
        ContactsPatches.Pending.Add(dialog, nested);
        ContactsPatches.UpdateLoading(dialog);
        Check(ContactsPatches.SortRequested(dialog), "生成コールバック内の別イベントのソート要求も取り消さない");

        var disposed = new ContactsPatches.Loading(manager, Update) { Contacts = [normal] };
        ContactsPatches.Pending.Add(dialog, disposed);
        ContactsPatches.OnDispose(dialog);
        Check(!ContactsPatches.Pending.TryGetValue(dialog, out _) && ContactsPatches.UpdateLoading(dialog), "Disposeで初期生成状態を解放");
        var empty = new ContactsPatches.Loading(manager, Update) { Contacts = [] };
        ContactsPatches.Pending.Add(dialog, empty);
        Check(!ContactsPatches.UpdateLoading(dialog) && !ContactsPatches.Pending.TryGetValue(dialog, out _), "0件でも終了");
        Exception? reported = null;
        ContactsPatches.ReportError = error => reported = error;
        var failure = new InvalidOperationException("検査用");
        var failed = new ContactsPatches.Loading(manager, _ => throw failure) { Contacts = [data("failure", OnlineStatus.Offline)] };
        ContactsPatches.Pending.Add(dialog, failed);
        ContactsPatches.UpdateLoading(dialog);
        Check(ReferenceEquals(reported, failure) && !failed.Creating && !ContactsPatches.Pending.TryGetValue(dialog, out _) && ContactsPatches.SortRequested(dialog), "例外で記録・状態解放・本体ソートへ復帰");
    }

    private static ContactItem Item(ContactData data, bool unread = false)
    {
        var slot = Empty<Slot>();
        var item = Empty<ContactItem>();
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(item, [slot]);
        AccessTools.PropertySetter(typeof(ContactItem), "Contact").Invoke(item, [data.Contact]);
        AccessTools.PropertySetter(typeof(ContactItem), "Data").Invoke(item, [data]);
        var username = new Sync<string>();
        AccessTools.Field(typeof(SyncField<string>), "_value").SetValue(username, data.Contact.ContactUsername);
        AccessTools.Field(typeof(ContactItem), "_rawUsername").SetValue(item, username);
        AccessTools.Field(typeof(ContactItem), "_alternateNames").SetValue(item, new SyncFieldList<string>());
        var unreadRef = new SyncRef<Text>();
        var unreadText = Empty<Text>();
        var content = new Sync<string>();
        AccessTools.Field(typeof(SyncField<string>), "_value").SetValue(content, unread ? "1" : "");
        AccessTools.Field(typeof(Text), "Content").SetValue(unreadText, content);
        AccessTools.Field(typeof(ContactItem), "_unreadCount").SetValue(item, unreadRef);
        texts[unreadRef] = unreadText;
        rows[slot] = item;
        active[slot] = true;
        return item;
    }

    private static bool IsActive(ref bool __result) { __result = visible; return false; }
    private static bool SetActive(Slot __instance, bool __0) { activeWrites++; active[__instance] = __0; return false; }
    private static bool GetSearch(ref TextField __result) { __result = search; return false; }
    private static bool GetText(SyncRef<Text> __instance, ref Text __result) { __result = texts[__instance]; return false; }
    private static bool GetQuery(ref string __result) { __result = query; return false; }
    private static bool GetItem(Slot __instance, ref ContactItem __result) { __result = rows[__instance]; return false; }
    private static bool OpenPage(ContactsDialog __instance, string __0) { selections.Add(__0); Check(ContactsPatches.BeforeSelect(__instance, __0), "再開時の選択は本体へ通す"); return false; }
    private static T Empty<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { assertions++; return; }
        throw new InvalidOperationException("不適合なILを拒否しませんでした。");
    }
}
