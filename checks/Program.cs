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
    private static readonly Dictionary<Slot, long> orders = [];
    private static readonly List<Slot> changed = [];
    private static readonly Dictionary<SyncRef<Text>, Text> texts = [];
    private static readonly List<string> selections = [];
    private static readonly Dictionary<Slot, List<Component>> spacerComponents = [];
    private static readonly HashSet<Slot> destroyed = [];
    private static VerticalLayout? layout;
    private static string query = "";
    private static bool visible = true;
    private static int activeWrites;
    private static int queryReads;
    private static Slot? failWrite;
    private static Func<Component, bool> canRun = null!;

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
            var data = new ContactData(manager, new Contact { ContactUserId = id, ContactUsername = id, ContactStatus = ContactStatus.Accepted });
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
                var (start, end) = state.TakeBatch(10);
                Check(end > start && end - start <= 10, "1更新最大10件、必ず進む");
                visited.AddRange(Enumerable.Range(start, end - start));
            }
            Check(visited.SequenceEqual(Enumerable.Range(0, count)), "全件を1回ずつ走査");
        }
        Check(ContactsPatches.MatchesSearch("Alice", [], ContactsPatches.NormalizeQuery(" AL ")), "検索の前後空白・大小文字");
        Check(ContactsPatches.MatchesSearch("Alice", ["別名"], "別"), "別名の前方一致");
        Check(!ContactsPatches.MatchesSearch("Alice", [], "lic"), "部分一致には変えない");
        Check(ContactsPatches.MatchesSearch("Alice", [], ContactsPatches.NormalizeQuery("  ")), "空の検索");
        Check(ContactsPatches.NormalizeQuery(null) == "", "nullも検索解除");

        foreach (string name in new[] { "OnAttach", "OnCommonUpdate", "OnActivated", "UpdateContactItem", "SearchTextChanged" })
        {
            var method = AccessTools.DeclaredMethod(typeof(ContactsDialog), name);
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            int length = original.Count;
            Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transform = name switch {
                "OnAttach" => ContactsPatches.TranspileAttach,
                "SearchTextChanged" => ContactsPatches.TranspileSearch,
                _ => code => ContactsPatches.TranspileSort(code, method) };
            var changed = transform(original).ToList();
            Check(changed.Count == length, "ILの命令数を増やさない");
            Check(changed.Count(op => op.operand is MethodInfo m && m.DeclaringType == typeof(ContactsPatches))
                == 1, "生成・ソート・検索の必要箇所だけ差し替え");
            if (name == "UpdateContactItem")
                Check(changed.Count(op => op.Calls(AccessTools.Method(typeof(ContactsDialog), "AddContactItem"))) == 1,
                    "本体の行生成呼び出しを差し替えず残す");
            if (name == "SearchTextChanged")
            {
                var setter = AccessTools.PropertySetter(typeof(Slot), "ActiveSelf");
                Check(changed.Count(op => op.Calls(setter)) == 1, "通常検索のActiveSelf書込みは本体のまま残す");
                var invalid = PatchProcessor.GetOriginalInstructions(method).ToList();
                invalid[invalid.FindIndex(op => op.Calls(setter)) - 1].opcode = OpCodes.Ldc_I4_0;
                Reject(() => transform(invalid).ToList());
            }
            Reject(() => transform([]).ToList());
            var doubled = PatchProcessor.GetOriginalInstructions(method).ToList();
            Reject(() => transform(doubled.Concat(doubled)).ToList());
        }
        var generate = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ContactsPatches), "Generate")).ToList();
        int update = generate.FindIndex(op => op.Calls(AccessTools.Method(typeof(Action<ContactData>), "Invoke")));
        int setOrder = generate.FindIndex(op => op.Calls(AccessTools.PropertySetter(typeof(Slot), "OrderOffset")));
        Check(update >= 0 && setOrder > update, "本体の生成が戻ってからOrderOffsetを設定");
        Check(!typeof(ContactsPatches).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SelectMany(method => PatchProcessor.GetOriginalInstructions(method))
            .Any(op => op.Calls(AccessTools.PropertySetter(typeof(UIBuilder), "ForceNext"))), "UIBuilder.ForceNextに介入しない");
        Check(!typeof(ContactsPatches).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SelectMany(method => PatchProcessor.GetOriginalInstructions(method))
            .Any(op => op.operand is MethodInfo m && m.Name is "SortChildren" or "RunInUpdates"), "全体ソートと更新予約を持たない");

        var boundary = new Harmony("SmoothContacts.Checks.Boundary");
        void Patch(MethodInfo target, string prefix) => boundary.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
        try
        {
            canRun = AccessTools.MethodDelegate<Func<Component, bool>>(AccessTools.DeclaredPropertyGetter(typeof(Component), "CanRunUpdates"));
            Patch(AccessTools.PropertyGetter(typeof(Slot), "IsActive"), nameof(IsActive));
            Patch(AccessTools.PropertyGetter(typeof(Slot), "ActiveSelf"), nameof(GetActive));
            Patch(AccessTools.PropertyGetter(typeof(Slot), "OrderOffset"), nameof(GetOrder));
            Patch(AccessTools.PropertySetter(typeof(Slot), "OrderOffset"), nameof(SetOrder));
            Patch(AccessTools.PropertySetter(typeof(Slot), "ActiveSelf"), nameof(SetActive));
            Patch(AccessTools.Method(typeof(Slot), "AddSlot", [typeof(string), typeof(bool)]), nameof(AddSlot));
            Patch(AccessTools.Method(typeof(Slot), "Destroy", Type.EmptyTypes), nameof(DestroySlot));
            Patch(AccessTools.DeclaredMethod(typeof(ContainerWorker<Component>), "AttachComponentInternal"), nameof(Attach));
            Patch(AccessTools.PropertySetter(typeof(SyncField<float>), "Value"), nameof(SetFloat));
            Patch(AccessTools.PropertyGetter(typeof(SyncRef<Text>), "Target"), nameof(GetText));
            Patch(AccessTools.PropertyGetter(typeof(TextField), "TargetString"), nameof(GetQuery));
            Patch(AccessTools.PropertyGetter(typeof(TextEditor), "TargetString"), nameof(GetQuery));
            var getItem = typeof(ContainerWorker<Component>).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(ContactItem));
            Patch(getItem, nameof(GetItem));
            Patch(AccessTools.DeclaredMethod(typeof(ContactsDialog), "OpenContactPage"), nameof(OpenPage));
            CheckNativeOrder(manager, Data);
            // 参照型のジェネリックgetterは実行コードを共有するため、検査の段階ごとに置換する。
            boundary.Unpatch(AccessTools.PropertyGetter(typeof(SyncRef<Text>), "Target"), HarmonyPatchType.All, boundary.Id);
            Patch(AccessTools.PropertyGetter(typeof(SyncRef<TextField>), "Target"), nameof(GetSearch));
            boundary.Patch(AccessTools.DeclaredMethod(typeof(ContactsDialog), "SearchTextChanged"),
                prefix: new HarmonyMethod(typeof(ContactsPatches), nameof(ContactsPatches.BeforeSearch)),
                transpiler: new HarmonyMethod(typeof(ContactsPatches), nameof(ContactsPatches.TranspileSearch)));
            CheckLoading(manager, current, Data);
            CheckSelection(manager, Data);
            CheckRestoring(manager, Data);
            CheckSpacer(manager, current, Data);
            CheckPauses(manager, Data);
        }
        finally { boundary.UnpatchSelf(); ContactsPatches.ReportError = null; }

        var harmony = new Harmony("SmoothContacts.Checks");
        try
        {
            ContactsPatches.Apply(harmony);
            foreach (string name in new[] { "OnAttach", "OnCommonUpdate", "OnActivated", "UpdateContactItem", "OpenContactPage", "set_SelectedContactId", "SearchTextChanged", "OnDispose" })
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
            Check(config.Count == 2 && ContactsPatches.BatchSize.Value == 5 && ContactsPatches.PauseUpdates.Value == 1,
                "設定の既定値は5件・1更新休止");
            Check(config.Keys.All(key => key.Section == "Loading") && config.Keys.Select(key => key.Key)
                .Order().SequenceEqual(new[] { "Batch size", "Updates to skip" }), "設定UIには英語の2項目だけ登録");
            var state = new ContactsPatches.Loading(Empty<ContactManager>(), _ => { }) {
                Contacts = Enumerable.Repeat<ContactData>(null!, 100).ToList() };
            Check(state.TakeBatch(10) == (0, 10), "予算10件で進む");
            ContactsPatches.BatchSize.Value = 3;
            ContactsPatches.PauseUpdates.Value = 2;
            Check(state.TakeBatch(3) == (10, 13), "残り予算3件で進む");
            Check(state.TakeBatch(0) == (13, 13), "検索で使い切った更新では生成しない");
            config.Save();
            ContactsPatches.BindSettings(new ConfigFile(path, false) { SaveOnConfigSet = false });
            Check(ContactsPatches.BatchSize.Value == 3 && ContactsPatches.PauseUpdates.Value == 2, "両設定の保存と再読込");
            Check(state.TakeBatch(int.MaxValue) == (13, 100), "大きな予算でも全件数を超えない");
            Check(state.TakeBatch(10) == (100, 100), "完了後は進まない");
            Check((int)ContactsPatches.BatchSize.DefaultValue == 5 && (int)ContactsPatches.PauseUpdates.DefaultValue == 1,
                "保存値の変更後もリセット先の既定値は5・1");
        }
        finally { File.Delete(path); ContactsPatches.BatchSize.Value = 10; ContactsPatches.PauseUpdates.Value = 0; }
    }

    private static void CheckPauses(ContactManager manager, Func<string, OnlineStatus, ContactData> data)
    {
        var dialog = Empty<ContactsDialog>();
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(dialog, [root]);
        AccessTools.Field(typeof(ContactsDialog), "_searchBar").SetValue(dialog, new SyncRef<TextField>());
        ContactsPatches.Items(dialog) = [];
        SetListUI(dialog);
        query = ""; visible = true;
        ContactsPatches.BatchSize.Value = 5;
        ContactsPatches.PauseUpdates.Value = 1;
        var state = new ContactsPatches.Loading(manager, entry => ContactsPatches.Items(dialog)[entry.UserId] = Item(entry))
            { ReservedRows = 0 };
        ContactsPatches.Pending.Add(dialog, state);
        try
        {
            Tick(dialog); Tick(dialog);
            (state.Contacts, state.Order) = ContactsPatches.Prepare(Enumerable.Range(0, 30)
                .Select(i => data($"pause{i:D2}", OnlineStatus.Offline)).ToList(), _ => 0);
            Tick(dialog);
            Check(state.Position == 5 && ContactsPatches.Items(dialog).Count == 5, "準備待ち後も最初の更新ですぐ5件生成");
            visible = false;
            Check(!ContactsPatches.BeforeSelect(dialog, "pause29"), "非アクティブ中の未生成相手の選択を保持");
            Tick(dialog); Tick(dialog);
            visible = true;
            int reads = queryReads;
            Tick(dialog);
            Check(state.Position == 5 && ContactsPatches.Items(dialog).ContainsKey("pause29") && state.Selection == null,
                "非アクティブ中に休止は進まず、再開した休止更新でも保留選択は実行");
            Check(queryReads == reads + 1, "休止更新は選択先の検索判定だけ行う");
            Check(ContactsPatches.BeforeSelect(dialog, "pause28") && ContactsPatches.Items(dialog).ContainsKey("pause28"),
                "明示的選択はバッチ間隔を待たず生成");
            Tick(dialog);
            Check(state.Position == 10, "1更新休んだ次の更新で続き5件");
            ContactsPatches.PauseUpdates.Value = 0;
            Tick(dialog);
            Check(state.Position == 15, "休止を0に変更すると残り待機も解除");
            ContactsPatches.PauseUpdates.Value = 2;
            Tick(dialog);
            Check(state.Position == 20, "休止回数の増加は次の処理後に適用");
            reads = queryReads;
            Tick(dialog); Tick(dialog);
            Check(state.Position == 20 && queryReads == reads, "指定した2更新は生成・検索文字列取得を休む");
            Tick(dialog);
            Check(state.Position == 25, "2更新の休止後に生成を再開");
            ContactsPatches.SortRequested(dialog) = true;
            ContactsPatches.PauseUpdates.Value = -1;
            Check(!Tick(dialog) && !ContactsPatches.Pending.TryGetValue(dialog, out _), "負の休止は0扱い、最終生成更新ではソートしない");
            Check(Tick(dialog), "完了後の本体ソートを休止設定で遅らせない");

            var searchChanged = AccessTools.MethodDelegate<Action<ContactsDialog, TextEditor>>(AccessTools.DeclaredMethod(typeof(ContactsDialog), "SearchTextChanged"));
            void Search(string value) { query = value; searchChanged(dialog, Empty<TextEditor>()); }
            int CountActive() => ContactsPatches.Items(dialog).Values.Count(item => active[item.Slot]);
            ContactsPatches.PauseUpdates.Value = 1;
            Search("missing"); Search("");
            Tick(dialog);
            Check(CountActive() == 5, "検索解除の初回もすぐ5件再表示");
            reads = queryReads;
            Check(Tick(dialog) && CountActive() == 5 && queryReads == reads, "再表示だけの休止では本体ソート要求を通す");
            Tick(dialog);
            Check(CountActive() == 10, "検索解除も1更新置きに進む");
            Search("pause29");
            Check(CountActive() == 1 && active[ContactsPatches.Items(dialog)["pause29"].Slot], "休止中でも完全一致の検索を即時表示");
            Search(""); Tick(dialog);
            Check(CountActive() == 6, "再検索は旧休止を取り消して新しい復帰を開始");
            ContactsPatches.PauseUpdates.Value = int.MaxValue;
            Tick(dialog); Tick(dialog);
            Check(CountActive() == 11, "非常に大きい休止設定も次の処理後に適用");
            Tick(dialog);
            Check(CountActive() == 11, "大きな休止設定でもオーバーフローしない");
            ContactsPatches.PauseUpdates.Value = int.MinValue;
            Tick(dialog); Tick(dialog);
            Check(CountActive() == 21, "非常に小さい休止設定も毎更新処理として扱う");
            ContactsPatches.PauseUpdates.Value = 1;
            Tick(dialog); Tick(dialog); Tick(dialog);
            Check(CountActive() == 30 && !ContactsPatches.Restoring.TryGetValue(dialog, out _), "休止を挟んで全件復帰しキューを解放");

            // 初期生成と復帰が重なっても、一つの予算と休止を使う。
            ContactsPatches.Items(dialog).Clear();
            foreach (var entry in state.Contacts.Take(3))
            {
                var item = Item(entry);
                ContactsPatches.Items(dialog)[entry.UserId] = item;
                active[item.Slot] = false;
                ContactsPatches.QueueRestore(item.Slot, dialog);
            }
            state.Position = 3;
            ContactsPatches.Pending.Add(dialog, state);
            Tick(dialog);
            Check(state.Position == 5 && CountActive() == 5, "復帰3件と生成2件を共通予算5件で処理");
            Tick(dialog);
            Check(state.Position == 5 && CountActive() == 5, "生成と復帰に共通の休止を1回適用");
            Tick(dialog);
            Check(state.Position == 10 && CountActive() == 10, "復帰終了後も同じ間隔で生成を継続");
            ContactsPatches.OnDispose(dialog);
            state.Position = 10;
            ContactsPatches.Pending.Add(dialog, state);
            Tick(dialog);
            Check(state.Position == 15, "Dispose後に古い休止を残さない");
        }
        finally
        {
            ContactsPatches.OnDispose(dialog);
            ContactsPatches.BatchSize.Value = 10; ContactsPatches.PauseUpdates.Value = 0;
            visible = true; query = "";
        }
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
        SetListUI(dialog);
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
        }
        var state = new ContactsPatches.Loading(manager, Update);
        ContactsPatches.Pending.Add(dialog, state);
        Check(!ContactsPatches.UpdateLoading(dialog) && created.Count == 0, "事前処理中は生成しない");
        Check(!ContactsPatches.BeforeSelect(dialog, "row30") && state.Selection == "row30", "事前処理中の選択を保持");
        state.Selection = null;
        (state.Contacts, state.Order) = ContactsPatches.Prepare(Enumerable.Range(0, 35).Select(i => data($"row{i:D2}", OnlineStatus.Offline)).ToList(), _ => 0);
        ContactsPatches.SortRequested(dialog) = false;
        Check(!ContactsPatches.UpdateLoading(dialog) && created.Count == 10 && active.Values.All(v => v), "10件ずつ最初から表示");
        Check(ContactsPatches.Items(dialog).All(pair => orders[pair.Value.Slot] == state.Order[pair.Key] * 100L),
            "生成した更新内で全行へ事前順位を設定");
        Check(!ContactsPatches.SortRequested(dialog) && activeWrites == 0, "初期生成のソート要求を除き、空の検索ではActiveを書き直さない");
        visible = false;
        ContactsPatches.RequestActivationSort(dialog, true);
        Check(!ContactsPatches.SortRequested(dialog), "生成中の再表示ソート要求を除く");
        for (int i = 0; i < 3; i++) Check(!Tick(dialog) && created.Count == 10 && state.Position == 10, "親が非アクティブなら本体の判定で生成停止");
        Check(!ContactsPatches.BeforeSelect(dialog, "row30"), "非アクティブ中の選択を保留");
        visible = true;
        ContactsPatches.BeforeSearch(dialog);
        query = "row2";
        foreach (var item in ContactsPatches.Items(dialog).Values) active[item.Slot] = ContactsPatches.MatchesSearch(item.Username, item.AlternateNames, query);
        ContactsPatches.UpdateLoading(dialog);
        Check(created.Count == 21 && selections.SequenceEqual(new[] { "row30" }) && state.Selection == null, "再表示で選択先1件と続き10件を生成");
        Check(!active[ContactsPatches.Items(dialog)["row30"].Slot], "選択先にも途中の検索条件を適用");
        Check(orders[ContactsPatches.Items(dialog)["row30"].Slot] == state.Order["row30"] * 100L,
            "先行選択にも同じ生成後の順位設定を適用");
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

    private static void CheckSelection(ContactManager manager, Func<string, OnlineStatus, ContactData> data)
    {
        var dialog = Empty<ContactsDialog>();
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(dialog, [root]);
        AccessTools.Field(typeof(ContactsDialog), "_searchBar").SetValue(dialog, new SyncRef<TextField>());
        SetListUI(dialog);
        var existing = data("select-existing", OnlineStatus.Online);
        var cloud = Item(data("select-cloud", OnlineStatus.Offline));
        cloud.Contact.ContactStatus = ContactStatus.SearchResult;
        ContactsPatches.Items(dialog) = new() { [existing.UserId] = Item(existing) };
        ((List<ContactItem>)AccessTools.Field(typeof(ContactsDialog), "_searchResultItems").GetValue(dialog)!).Add(cloud);
        var state = new ContactsPatches.Loading(manager, _ => throw new InvalidOperationException("既存行の選択で生成した"));
        ContactsPatches.Pending.Add(dialog, state);
        foreach (bool prepared in new[] { false, true })
        foreach (bool shown in new[] { false, true })
        {
            state.Contacts = prepared ? [] : null;
            visible = shown;
            foreach (string id in new[] { existing.UserId, cloud.Contact.ContactUserId })
            {
                state.Selection = "older-selection";
                Check(ContactsPatches.BeforeSelect(dialog, id) && state.Selection == null && state.Position == 0,
                    "生成済みのフレンド・検索結果は即時通過し、古い選択待ちを残さない");
            }
        }
        state.Contacts = null; visible = true;
        Check(!ContactsPatches.BeforeSelect(dialog, "not-ready"), "未生成の相手は準備完了まで待つ");
        var item = ContactsPatches.Items(dialog)[existing.UserId];
        active[item.Slot] = false;
        ContactsPatches.QueueRestore(item.Slot, dialog);
        Check(!ContactsPatches.UpdateLoading(dialog) && active[item.Slot] && state.Contacts == null,
            "準備待ちでも検索復帰を進め、行生成は始めない");
        ContactsPatches.BeforeSelect(dialog, existing.UserId);
        int opened = selections.Count;
        state.Contacts = [];
        Tick(dialog);
        Check(selections.Count == opened && !ContactsPatches.Pending.TryGetValue(dialog, out _),
            "準備完了後に古い選択を再実行しない");
    }

    private static void CheckRestoring(ContactManager manager, Func<string, OnlineStatus, ContactData> data)
    {
        rows.Clear(); active.Clear(); orders.Clear(); changed.Clear();
        query = ""; visible = true;
        var dialog = Empty<ContactsDialog>();
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(dialog, [root]);
        AccessTools.Field(typeof(ContactsDialog), "_searchBar").SetValue(dialog, new SyncRef<TextField>());
        AccessTools.Field(typeof(ContactsDialog), "_searchResultItems").SetValue(dialog, new List<ContactItem>());
        ContactsPatches.Items(dialog) = [];
        SetListUI(dialog);
        var errors = new List<Exception>();
        ContactsPatches.ReportError = errors.Add;
        var searchChanged = AccessTools.MethodDelegate<Action<ContactsDialog, TextEditor>>(AccessTools.DeclaredMethod(typeof(ContactsDialog), "SearchTextChanged"));
        void Search(string value) { query = value; searchChanged(dialog, Empty<TextEditor>()); }
        int CountActive() => ContactsPatches.Items(dialog).Values.Count(item => active[item.Slot]);
        int Waiting() => ContactsPatches.Restoring.TryGetValue(dialog, out var queue) ? queue.Count : 0;
        void Drain()
        {
            for (int updates = 0; Waiting() > 0; updates++)
            {
                Check(updates < 100, "再表示の保留が有限回で完了");
                int writes = activeWrites;
                Tick(dialog);
                Check(activeWrites - writes <= Math.Max(1, ContactsPatches.BatchSize.Value), "検索解除の再表示も設定件数以内");
            }
        }
        for (int i = 0; i < 25; i++)
        {
            var item = Item(data($"filter{i:D2}", OnlineStatus.Offline));
            ContactsPatches.Items(dialog)[item.Data.UserId] = item;
            orders[item.Slot] = (24 - i) * 100L;
        }
        Search("");
        Check(Waiting() == 0 && changed.Count == 0, "全員表示済みなら空欄でも余分な保留・書込みなし");
        Search("missing");
        Check(Waiting() == 0 && CountActive() == 0, "非空検索は不一致の全行をその場で隠す");
        Search(" FILTER00 ");
        Check(Waiting() == 0 && CountActive() == 1 && active[ContactsPatches.Items(dialog)["filter00"].Slot],
            "末尾順位の完全一致も通常更新を待たず表示する");
        Search("missing");
        changed.Clear();
        Search(" \t ");
        Check(Waiting() == 25 && CountActive() == 0 && changed.Count == 0, "空白だけなら本体の空欄分岐から再表示を保留");
        queryReads = 0;
        Tick(dialog);
        Check(Waiting() == 15 && CountActive() == 10 && queryReads == 0, "再表示は1更新10件、検索文字列の取得・再判定なし");
        Check(changed.Select(slot => rows[slot].Data.UserId).SequenceEqual(Enumerable.Range(15, 10).Reverse().Select(i => $"filter{i:D2}")),
            "再表示は辞書の挿入順ではなく現在のOrderOffset順");
        Search("filter00");
        Check(Waiting() == 0 && CountActive() == 1 && active[ContactsPatches.Items(dialog)["filter00"].Slot],
            "再表示途中の検索は旧キューを破棄し、完全一致を即時表示");
        int before = activeWrites;
        Tick(dialog);
        Check(CountActive() == 1 && activeWrites == before, "次の更新に古い検索解除のONが漏れない");
        Search("filter2");
        Check(Waiting() == 0 && CountActive() == 5 && ContactsPatches.Items(dialog).Values.Where(item => active[item.Slot]).All(item => item.Username.StartsWith("filter2")),
            "新しい検索の一致ON・不一致OFFを両方即時反映");
        Search("");
        visible = false;
        before = activeWrites;
        for (int i = 0; i < 3; i++) Tick(dialog);
        Check(activeWrites == before && Waiting() == 20, "検索解除途中も非アクティブ中は停止");
        visible = true;
        ContactsPatches.BatchSize.Value = 3; Tick(dialog);
        Check(CountActive() == 8 && Waiting() == 17, "検索解除の件数変更を次の更新へ反映");
        ContactsPatches.BatchSize.Value = 10; Drain();
        Check(CountActive() == 25 && !ContactsPatches.SortRequested(dialog), "再表示を完了し、追加ソートしない");
        Search("");
        Check(Waiting() == 0 && CountActive() == 25, "全件表示後の空欄イベントで余分な保留を作らない");
        foreach (int value in new[] { 0, -1, int.MinValue })
        {
            Search("missing"); Search(""); ContactsPatches.BatchSize.Value = value; Tick(dialog);
            Check(CountActive() == 1 && Waiting() == 24, "0以下の設定でも1件ずつ再表示");
            ContactsPatches.BatchSize.Value = int.MaxValue; Drain();
            Check(CountActive() == 25 && Waiting() == 0, "大きな設定でも対象件数で終了");
        }
        ContactsPatches.BatchSize.Value = 10;
        Search("missing"); Search("");
        var removed = ContactsPatches.Items(dialog)["filter24"];
        AccessTools.PropertySetter(typeof(Worker), "IsDisposed").Invoke(removed, [true]);
        ContactsPatches.Items(dialog).Remove("filter24"); active.Remove(removed.Slot);
        var renamed = ContactsPatches.Items(dialog)["filter23"];
        AccessTools.Field(typeof(SyncField<string>), "_value").SetValue(AccessTools.Field(typeof(ContactItem), "_rawUsername").GetValue(renamed), "renamed");
        active[renamed.Slot] = true;
        before = activeWrites;
        Drain();
        Check(errors.Count == 0 && CountActive() == 24 && activeWrites - before == 23,
            "削除済みと先行表示済みを飛ばし、名前変更に関係なく残りをON");

        ContactsPatches.Items(dialog).Clear(); rows.Clear(); active.Clear(); orders.Clear(); query = "";
        for (int i = 0; i < 3; i++)
        {
            var item = Item(data($"existing{i}", OnlineStatus.Offline));
            ContactsPatches.Items(dialog)[item.Data.UserId] = item;
        }
        int generated = 0;
        var state = new ContactsPatches.Loading(manager, entry => { ContactsPatches.Items(dialog)[entry.UserId] = Item(entry); generated++; });
        (state.Contacts, state.Order) = ContactsPatches.Prepare(Enumerable.Range(0, 10).Select(i => data($"new{i}", OnlineStatus.Offline)).ToList(), _ => 0);
        ContactsPatches.Pending.Add(dialog, state);
        Search("new");
        Check(Waiting() == 0 && CountActive() == 0, "初期生成中の検索も既存の不一致行を即時OFF");
        Search(""); before = activeWrites; queryReads = 0; Tick(dialog);
        Check(generated == 7 && activeWrites - before == 3 && queryReads == 1, "再表示3件と生成7件で合計10件、文字列取得は生成用1回");
        Check(CountActive() == 10 && !ContactsPatches.SortRequested(dialog), "生成した行はその更新で表示し、全行完成を待たない");
        Search("new"); Tick(dialog);
        Check(generated == 10 && CountActive() == 10 && Waiting() == 0 && !ContactsPatches.Pending.TryGetValue(dialog, out _), "再検索で生成を妨げず、新規行にも検索を適用");
        Search(""); Tick(dialog);
        Check(CountActive() == 13, "生成完了後も隠れた行だけを復帰");
        Search("missing"); Search("");
        failWrite = ContactsPatches.Items(dialog)["existing0"].Slot;
        Drain(); failWrite = null;
        Check(errors.Count == 1 && CountActive() == 12, "個別行の再表示エラーを記録して他の行を継続");
        Search(""); ContactsPatches.Pending.Add(dialog, state);
        ContactsPatches.OnDispose(dialog);
        Check(!ContactsPatches.Pending.TryGetValue(dialog, out _) && Waiting() == 0, "Disposeで生成と検索の両方を解放");
    }

    private static void CheckSpacer(ContactManager manager, Dictionary<string, ContactData> current, Func<string, OnlineStatus, ContactData> data)
    {
        var dialog = Empty<ContactsDialog>();
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(dialog, [root]);
        AccessTools.Field(typeof(ContactsDialog), "_searchBar").SetValue(dialog, new SyncRef<TextField>());
        AccessTools.Field(typeof(ContactsDialog), "_searchResultItems").SetValue(dialog, new List<ContactItem>());
        var errors = new List<Exception>();
        ContactsPatches.ReportError = errors.Add;
        bool fail = false;
        ContactsPatches.Loading Begin(int count)
        {
            SetListUI(dialog);
            ContactsPatches.Items(dialog) = [];
            query = ""; visible = true; fail = false;
            var state = new ContactsPatches.Loading(manager, entry =>
            {
                if (fail) throw new InvalidOperationException("生成中断の検査");
                if (!entry.Contact.ShouldBeHidden) ContactsPatches.Items(dialog)[entry.UserId] = Item(entry);
            });
            (state.Contacts, state.Order) = ContactsPatches.Prepare(
                Enumerable.Range(0, count).Select(i => data($"height{i:D3}", OnlineStatus.Offline)).ToList(), _ => 0);
            ContactsPatches.Pending.Add(dialog, state);
            return state;
        }
        float Height(Slot slot)
        {
            var element = spacerComponents[slot].OfType<LayoutElement>().Single();
            Check(element.MinHeight.Value == element.PreferredHeight.Value, "空白の最小・希望高さが一致");
            return element.PreferredHeight.Value;
        }
        var state = Begin(100);
        var hidden = data("hidden", OnlineStatus.Offline);
        hidden.Contact.ContactStatus = ContactStatus.Ignored;
        Check(hidden.Contact.ShouldBeHidden, "非表示Contactを検査に含める");
        state.Contacts!.Add(hidden);
        int before = spacerComponents.Count;
        Tick(dialog);
        var spacer = state.Spacer!;
        Check(state.ReservedRows == 100 && Height(spacer) == 3236,
            $"非表示対象を除き、10行生成後は残り90行分を予約: 予約={state.ReservedRows}, 生成={ContactsPatches.Items(dialog).Count}, エラー={string.Join(';', errors)}");
        Check(orders[spacer] == long.MaxValue && spacerComponents[spacer].Select(c => c.GetType())
            .SequenceEqual(new[] { typeof(RectTransform), typeof(LayoutElement) }), "末尾の単一Slotは描画・操作要素を持たない");
        visible = false; Tick(dialog);
        Check(state.Position == 10 && Height(spacer) == 3236, "非アクティブ中は生成も空白も進まない");
        visible = true;
        Check(ContactsPatches.BeforeSelect(dialog, "height090") && Height(spacer) == 3200, "先行選択の1行分も予約から引く");
        while (ContactsPatches.Pending.TryGetValue(dialog, out _))
        {
            Tick(dialog);
            int count = ContactsPatches.Items(dialog).Count;
            float total = state.Spacer == null ? count * 32 + (count - 1) * 4 : count * 36 + Height(spacer);
            Check(total == 3596, "全更新で完成時と同じ高さを保つ（外側余白を除く）");
        }
        Check(state.Spacer == null && destroyed.Contains(spacer) && spacerComponents.Count == before + 1,
            "空白は一つを使い回し、完了時に除去");

        state = Begin(10); before = spacerComponents.Count; Tick(dialog);
        Check(state.Spacer == null && spacerComponents.Count == before, "1更新で終われば空白を作らない");
        state = Begin(11); layout!.Spacing.Value = 6; Tick(dialog);
        Check(Height(state.Spacer!) == 32, "残り1行では末尾の余分な間隔を予約しない");
        ContactsPatches.OnDispose(dialog);
        state = Begin(23); layout!.Spacing.Value = 6; Tick(dialog);
        Check(Height(state.Spacer!) + 10 * 38 == 23 * 32 + 22 * 6, "一覧の現在の間隔で高さを合わせる");
        ContactsPatches.OnDispose(dialog);
        state = Begin(30); Tick(dialog); spacer = state.Spacer!;
        var searchChanged = AccessTools.MethodDelegate<Action<ContactsDialog, TextEditor>>(AccessTools.DeclaredMethod(typeof(ContactsDialog), "SearchTextChanged"));
        query = "height000"; searchChanged(dialog, Empty<TextEditor>());
        Check(state.Spacer == null && destroyed.Contains(spacer) && ContactsPatches.Items(dialog).Values.Count(i => active[i.Slot]) == 1,
            "検索は空白を除き、完全一致行を即時表示");
        query = ""; searchChanged(dialog, Empty<TextEditor>()); Tick(dialog);
        Check(state.Spacer == null && state.ReservedRows == 0, "検索解除後も空白を作り直さない");
        ContactsPatches.OnDispose(dialog);

        foreach (string reason in new[] { "dispose", "failure", "nonfriend", "hidden", "removed" })
        {
            state = Begin(21); Tick(dialog); spacer = state.Spacer!;
            if (reason == "dispose") ContactsPatches.OnDispose(dialog);
            if (reason == "failure") { fail = true; Tick(dialog); }
            if (reason == "nonfriend") ContactsPatches.BeforeSelect(dialog, "unknown-nonfriend");
            if (reason == "hidden") ContactsPatches.BeforeSelect(dialog, hidden.UserId);
            if (reason == "removed")
            {
                foreach (var entry in state.Contacts!.Skip(10)) current.Remove(entry.UserId);
                Tick(dialog); Tick(dialog);
            }
            Check(state.Spacer == null && destroyed.Contains(spacer), $"空白が残らない: {reason}");
            ContactsPatches.OnDispose(dialog);
        }
        Check(errors.Count == 1, "意図した生成失敗だけを記録");
        state = Begin(21); layout = null; before = spacerComponents.Count; Tick(dialog);
        Check(state.ReservedRows == 0 && spacerComponents.Count == before && state.Position == 10, "VerticalLayoutがなければ高さ確保だけを省く");
        ContactsPatches.OnDispose(dialog);
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
        orders[slot] = rows.Count * 100L;
        return item;
    }

    private static bool IsActive(ref bool __result) { __result = visible; return false; }
    private static bool GetActive(Slot __instance, ref bool __result) { __result = active[__instance]; return false; }
    private static bool GetOrder(Slot __instance, ref long __result) { __result = orders[__instance]; return false; }
    private static bool SetActive(Slot __instance, bool __0) { if (__instance == failWrite) throw new InvalidOperationException("検査用の反映エラー"); activeWrites++; active[__instance] = __0; changed.Add(__instance); return false; }
    private static bool GetSearch(ref TextField __result) { __result = search; return false; }
    private static bool GetText(SyncRef<Text> __instance, ref Text __result) { __result = texts[__instance]; return false; }
    private static bool GetQuery(ref string __result) { queryReads++; __result = query; return false; }
    private static bool GetItem(Slot __instance, ref object? __result)
    {
        __result = __instance == root ? layout : spacerComponents.TryGetValue(__instance, out var components)
            ? components.OfType<LayoutElement>().Single() : rows[__instance];
        return false;
    }
    private static void SetListUI(ContactsDialog dialog)
    {
        AccessTools.Field(typeof(ContactsDialog), "_searchResultItems").SetValue(dialog, new List<ContactItem>());
        var ui = Empty<UIBuilder>();
        AccessTools.Field(typeof(UIBuilder), "roots").SetValue(ui, new Stack<Slot>([root]));
        AccessTools.Field(typeof(ContactsDialog), "listUi").SetValue(dialog, ui);
        layout = Empty<VerticalLayout>();
        var spacing = new Sync<float>();
        AccessTools.Field(typeof(SyncField<float>), "_value").SetValue(spacing, 4f);
        AccessTools.Field(typeof(DirectionalLayout), "Spacing").SetValue(layout, spacing);
    }
    private static bool SetOrder(Slot __instance, long __0) { orders[__instance] = __0; return false; }
    private static bool AddSlot(string __0, ref Slot __result)
    {
        Check(__0 == "SmoothContacts Spacer", "高さ確保以外のSlotを追加しない");
        __result = Empty<Slot>(); spacerComponents[__result] = []; return false;
    }
    private static bool Attach(Slot __instance, Component __0)
    {
        AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(__0, [__instance]);
        foreach (var field in __0.GetType().GetFields().Where(f => f.FieldType == typeof(Sync<float>)))
            field.SetValue(__0, new Sync<float>());
        spacerComponents[__instance].Add(__0);
        return false;
    }
    private static bool DestroySlot(Slot __instance)
    {
        Check(spacerComponents.ContainsKey(__instance) && destroyed.Add(__instance), "所有する空白Slotだけを一度除去");
        return false;
    }
    private static bool SetFloat(SyncField<float> __instance, float __0)
    {
        AccessTools.Field(typeof(SyncField<float>), "_value").SetValue(__instance, __0); return false;
    }
    private static bool OpenPage(ContactsDialog __instance, string __0) { selections.Add(__0); Check(ContactsPatches.BeforeSelect(__instance, __0), "再開時の選択は本体へ通す"); return false; }
    private static T Empty<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static bool Tick(ContactsDialog dialog) => canRun(dialog) && ContactsPatches.UpdateLoading(dialog);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { assertions++; return; }
        throw new InvalidOperationException("不適合なILを拒否しませんでした。");
    }
}
