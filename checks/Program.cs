using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Configuration;
using ContactsLoading;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using SkyFrost.Base;

internal static class Program
{
    private static int assertions;
    private static readonly Dictionary<Slot, bool> active = [];
    private static readonly Queue<Action> scheduled = [];
    private static readonly Slot root = Empty<Slot>();
    private static readonly TextField search = Empty<TextField>();
    private static string query = "";
    private static int sorts;
    private static ContactsPatches.Loading? observed;

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
        foreach (var status in Enum.GetValues<OnlineStatus>().Append((OnlineStatus)999))
        {
            Check(ContactsPatches.Priority(1, status) == 0, "未読は状態によらず最優先");
            bool online = status is OnlineStatus.Sociable or OnlineStatus.Online or OnlineStatus.Away or OnlineStatus.Busy;
            Check(ContactsPatches.Priority(0, status) == (online ? 1 : 2), "オンライン4状態とその他の分類");
        }

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
        var input = new List<ContactData> { Data("other1"), Data("online", OnlineStatus.Online),
            Data("unread1"), Data("away", OnlineStatus.Away), Data("other2"), Data("unread2", OnlineStatus.Busy) };
        int reads = 0;
        var ordered = ContactsPatches.Prepare(input, data => { reads++; return data.UserId.StartsWith("unread") ? 1 : 0; });
        Check(ordered.Contacts.Select(d => d.UserId).SequenceEqual(new[] { "unread2", "unread1", "online", "away", "other1", "other2" }), "優先群の中も状態順に事前整列");
        Check(reads == 6 && ordered.PriorityEnd == 4, "未読取得は各1回、優先群の境界");
        var recent = Data("recent");
        recent.Contact.LatestMessageTime = new DateTime(2026, 10, 13);
        Check(ContactsPatches.Prepare([input[0], recent], _ => 0).Contacts[0] == recent, "最新メッセージを先に整列");
        var request = Data("request");
        request.Contact.ContactStatus = ContactStatus.Requested;
        Check(ContactsPatches.SortKey(request.Contact, OnlineStatus.Offline, false).Category == 2, "受信リクエストのカテゴリ");
        var nativeCategory = AccessTools.MethodDelegate<Func<ContactItem, int>>(AccessTools.Method(typeof(ContactsDialog), "GetOrderNumber"));
        foreach (var status in Enum.GetValues<OnlineStatus>())
        foreach (var contact in new[] { new Contact(), new Contact { ContactStatus = ContactStatus.Requested },
            new Contact { IsMigrated = true, IsCounterpartMigrated = false } })
        {
            var entry = new ContactData(manager, contact);
            entry.CurrentStatus.OnlineStatus = status;
            var item = Empty<ContactItem>();
            AccessTools.PropertySetter(typeof(ContactItem), "Contact").Invoke(item, [contact]);
            AccessTools.PropertySetter(typeof(ContactItem), "Data").Invoke(item, [entry]);
            Check(ContactsPatches.SortKey(contact, status, false).Category == nativeCategory(item), "ソートのカテゴリが実DLLの本体と一致");
        }

        foreach (int count in new[] { 0, 1, 9, 10, 11, 23, 32 })
        foreach (int priority in Enumerable.Range(0, count + 1))
        {
            var state = new ContactsPatches.Loading(manager, _ => { }) { Contacts = Enumerable.Repeat(input[0], count).ToList(), PriorityEnd = priority };
            var visited = new List<int>();
            int boundaries = 0;
            while (state.Position < count)
            {
                var (start, end) = state.TakeBatch();
                Check(end > start && end - start <= 10, "1更新最大10件、必ず進む");
                Check(!(start < priority && end > priority), "優先群の境界をまたがない");
                visited.AddRange(Enumerable.Range(start, end - start));
                if (start < priority && end == priority && end < count) boundaries++;
            }
            Check(visited.SequenceEqual(Enumerable.Range(0, count)), "全件を1回ずつ走査");
            Check(boundaries == (priority > 0 && priority < count ? 1 : 0), "優先分の公開境界は最大1回");
        }

        var dialog = Empty<ContactsDialog>();
        ContactsPatches.Items(dialog) = new();
        var selected = Data("selected");
        var deleted = Data("deleted");
        var replaced = Data("replaced");
        var normal = Data("normal");
        var created = new List<string>();
        var loading = new ContactsPatches.Loading(manager, data =>
        {
            created.Add(data.UserId);
            ContactsPatches.Items(dialog).Add(data.UserId, Empty<ContactItem>());
        }) { Contacts = [selected, deleted, replaced, normal], PriorityEnd = 1 };
        ContactsPatches.Pending.Add(dialog, loading);
        current.Remove(deleted.UserId);
        Data(replaced.UserId);
        ContactsPatches.BeforeSelect(dialog, selected.UserId);
        ContactsPatches.BeforeSelect(dialog, selected.UserId);
        foreach (var data in loading.Contacts) ContactsPatches.Generate(dialog, loading, data);
        Check(created.SequenceEqual(new[] { "selected", "normal" }), "指定先の優先生成、重複・削除・古いContactの除外");
        ContactsPatches.SortRequested(dialog) = true;
        Check(!ContactsPatches.ShouldSort(dialog), "生成中の毎更新ソートを抑制");
        ContactsPatches.OnDispose(dialog);
        Check(!ContactsPatches.Pending.TryGetValue(dialog, out _) && ContactsPatches.ShouldSort(dialog), "Disposeで状態解放とソート抑制解除");
        AccessTools.Method(typeof(ContactsPatches), "RunBatch").Invoke(null, [dialog, loading]);
        Check(created.Count == 2, "破棄後の古い予約では生成しない");

        Check(ContactsPatches.MatchesSearch("Alice", [], " AL "), "検索の前後空白・大小文字");
        Check(ContactsPatches.MatchesSearch("Alice", ["別名"], "別"), "別名の前方一致");
        Check(!ContactsPatches.MatchesSearch("Alice", [], "lic"), "部分一致には変えない");
        Check(ContactsPatches.MatchesSearch("Alice", [], "  "), "空の検索");

        foreach (string name in new[] { "OnAttach", "OnCommonUpdate", "UpdateContactItem" })
        {
            var method = AccessTools.DeclaredMethod(typeof(ContactsDialog), name);
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            var before = original.Select(op => (op.opcode, op.operand)).ToArray();
            Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transform = name switch {
                "OnAttach" => ContactsPatches.TranspileAttach, "OnCommonUpdate" => ContactsPatches.TranspileSort,
                _ => ContactsPatches.TranspileItem };
            var changed = transform(original).ToList();
            int added = name == "UpdateContactItem" ? 1 : 0;
            Check(changed.Count == before.Length + added, "IL命令数の変化は必要な引数追加だけ");
            Check(changed.Count(op => op.operand is MethodInfo methodInfo && methodInfo.DeclaringType == typeof(ContactsPatches)) == 1, "1か所だけ本Modへ差し替え");
            Reject(() => transform([]).ToList());
            var doubled = PatchProcessor.GetOriginalInstructions(method).ToList();
            Reject(() => transform(doubled.Concat(doubled)).ToList());
        }

        var create = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ContactsPatches), "CreateItem")).ToList();
        int setActive = create.FindIndex(op => op.Calls(AccessTools.PropertySetter(typeof(Slot), "ActiveSelf")));
        int setOrder = create.FindIndex(op => op.Calls(AccessTools.PropertySetter(typeof(Slot), "OrderOffset")));
        int attachRect = create.FindIndex(op => op.operand is MethodInfo m && m.Name == "GetComponentOrAttach" && m.GetGenericArguments().Contains(typeof(RectTransform)));
        Check(setActive > 0 && create[setActive - 1].opcode == OpCodes.Ldc_I4_0 && setActive < attachRect && setOrder < attachRect,
            "RectTransform取り付け前に非表示と順序を設定");
        var finish = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ContactsPatches), "Finish")).ToList();
        Check(finish.FindIndex(op => op.Calls(AccessTools.Method(typeof(Slot), "SortChildren")))
            < finish.FindIndex(op => op.Calls(AccessTools.Method(typeof(ContactsPatches), "Release"))), "最終ソートが公開より先");

        CheckPublication(manager, Data);

        var harmony = new Harmony("ContactsLoading.Checks");
        try
        {
            ContactsPatches.Apply(harmony);
            foreach (string name in new[] { "OnAttach", "OnCommonUpdate", "UpdateContactItem", "SearchTextChanged", "OpenContactPage", "set_SelectedContactId", "OnDispose" })
                Check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ContactsDialog), name))?.Owners.Contains(harmony.Id) == true,
                    $"実DLLへのパッチ適用: {name}");
        }
        finally { harmony.UnpatchSelf(); }
        Console.WriteLine($"PASS: {assertions}件の検査。ゲーム起動・ゲームファイル変更なし。");
    }

    private static void CheckSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ContactsLoading-check-{Guid.NewGuid():N}.cfg");
        try
        {
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            ContactsPatches.BindSettings(config);
            Check(config.Count == 1 && ContactsPatches.BatchSize.Value == 10, "設定は件数だけ、初期値10");
            var state = new ContactsPatches.Loading(Empty<ContactManager>(), _ => { }) {
                Contacts = Enumerable.Repeat<ContactData>(null!, 100).ToList(), PriorityEnd = 20 };
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
            Check(state.TakeBatch() == (16, 20), "大きな件数でも優先群の境界で止まる");
            Check(state.TakeBatch() == (20, 100), "残りにも同じ設定を適用し、件数を超えない");
            Check(state.TakeBatch() == (100, 100), "完了後は進まない");
        }
        finally
        {
            File.Delete(path);
            ContactsPatches.BatchSize.Value = 10;
        }
    }

    private static void CheckPublication(ContactManager manager, Func<string, OnlineStatus, ContactData> data)
    {
        // エンジンを起動せず、表示値の書込み・予約・ソートの境界だけを検査プロセス内で置換する。
        var harmony = new Harmony("ContactsLoading.Checks.Display");
        void Patch(MethodInfo target, string prefix) => harmony.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
        try
        {
            ContactsPatches.ReportError = error => throw new InvalidOperationException("公開処理の検査中に例外", error);
            Patch(AccessTools.PropertySetter(typeof(Slot), "ActiveSelf"), nameof(SetActive));
            Patch(AccessTools.PropertyGetter(typeof(SyncRef<Slot>), "Target"), nameof(GetRoot));
            Patch(AccessTools.PropertyGetter(typeof(SyncRef<TextField>), "Target"), nameof(GetSearch));
            Patch(AccessTools.PropertyGetter(typeof(TextField), "TargetString"), nameof(GetQuery));
            Patch(AccessTools.Method(typeof(Slot), "SortChildren"), nameof(Sort));
            Patch(AccessTools.DeclaredMethod(typeof(ComponentBase<Component>), "RunInUpdates", [typeof(int), typeof(Action)]), nameof(Schedule));
            var dialog = Empty<ContactsDialog>();
            ContactsPatches.Items(dialog) = [];
            AccessTools.Field(typeof(ContactsDialog), "_listRoot").SetValue(dialog, new SyncRef<Slot>());
            AccessTools.Field(typeof(ContactsDialog), "_searchBar").SetValue(dialog, new SyncRef<TextField>());
            var state = new ContactsPatches.Loading(manager, contact =>
            {
                var slot = Empty<Slot>();
                var item = Empty<ContactItem>();
                AccessTools.PropertySetter(typeof(Component), "Slot").Invoke(item, [slot]);
                var username = new Sync<string>();
                AccessTools.Field(typeof(SyncField<string>), "_value").SetValue(username, contact.UserId);
                AccessTools.Field(typeof(ContactItem), "_rawUsername").SetValue(item, username);
                AccessTools.Field(typeof(ContactItem), "_alternateNames").SetValue(item, new SyncFieldList<string>());
                ContactsPatches.Items(dialog)[contact.UserId] = item;
                observed!.Hidden[contact.UserId] = slot;
                active[slot] = false;
            });
            observed = state;
            state.Contacts = Enumerable.Range(0, 35).Select(i => data($"row{i:D2}", OnlineStatus.Online)).ToList();
            state.PriorityEnd = 13;
            state.Tick = () => AccessTools.Method(typeof(ContactsPatches), "RunBatch").Invoke(null, [dialog, state]);
            ContactsPatches.Pending.Add(dialog, state);
            ContactsPatches.SortRequested(dialog) = true;
            state.Tick();
            Check(active.Count == 10 && !active.Values.Any(v => v) && sorts == 0, "優先分の途中は全行非表示");
            scheduled.Dequeue()();
            Check(active.Count == 13 && active.Values.All(v => v) && sorts == 0, "優先分完成で一括公開、ソートなし");
            scheduled.Dequeue()();
            Check(active.Count == 23 && active.Values.Count(v => v) == 13, "残りは非表示で追加");
            foreach (var slot in active.Keys.ToArray()) active[slot] = true;
            AccessTools.Method(typeof(ContactsPatches), "AfterSearch").Invoke(null, [dialog]);
            Check(active.Values.Count(v => v) == 13, "検索でも未公開の行を表示しない");
            ContactsPatches.BeforeSelect(dialog, "row30");
            Check(active.Values.Count(v => v) == 14, "未生成の選択先だけ先行生成して公開");
            query = "row2";
            while (scheduled.TryDequeue(out var tick)) tick();
            Check(active.Count == 35 && sorts == 1 && !ContactsPatches.Pending.TryGetValue(dialog, out _), "全件生成後に1回だけソートして解放");
            Check(state.Hidden.Count == 0 && active.Values.Count(v => v) == 24, "残りの公開時に現在の検索条件を反映");
            Check(!ContactsPatches.SortRequested(dialog), "最終ソート直後の重複ソートを要求しない");

            foreach (var (count, priority) in new[] { (0, 0), (5, 5), (11, 0) })
            {
                active.Clear();
                ContactsPatches.Items(dialog).Clear();
                query = "";
                int previousSorts = sorts;
                var stage = new ContactsPatches.Loading(manager, state.Update) {
                    Contacts = Enumerable.Range(0, count).Select(i => data($"edge{i}", OnlineStatus.Offline)).ToList(), PriorityEnd = priority };
                observed = stage;
                stage.Tick = () => AccessTools.Method(typeof(ContactsPatches), "RunBatch").Invoke(null, [dialog, stage]);
                ContactsPatches.Pending.Add(dialog, stage);
                stage.Tick();
                while (scheduled.TryDequeue(out var tick)) tick();
                Check(active.Count == count && active.Values.All(v => v) && sorts == previousSorts + 1
                    && !ContactsPatches.Pending.TryGetValue(dialog, out _), "空・優先分のみ・残りのみでも公開と終了");
            }

            Exception? reported = null;
            ContactsPatches.ReportError = error => reported = error;
            var failure = new InvalidOperationException("検査用");
            var failed = new ContactsPatches.Loading(manager, _ => throw failure) { Contacts = [data("failure", OnlineStatus.Offline)] };
            ContactsPatches.Pending.Add(dialog, failed);
            AccessTools.Method(typeof(ContactsPatches), "RunBatch").Invoke(null, [dialog, failed]);
            Check(ReferenceEquals(reported, failure) && !ContactsPatches.Pending.TryGetValue(dialog, out _), "生成例外で記録と解放");
        }
        finally { harmony.UnpatchSelf(); ContactsPatches.ReportError = null; observed = null; }
    }

    private static bool SetActive(Slot __instance, bool __0) { active[__instance] = __0; return false; }
    private static bool GetRoot(ref Slot __result) { __result = root; return false; }
    private static bool GetSearch(ref TextField __result) { __result = search; return false; }
    private static bool GetQuery(ref string __result) { __result = query; return false; }
    private static bool Schedule(int __0, Action __1) { Check(__0 == 1, "次の更新へ予約"); scheduled.Enqueue(__1); return false; }
    private static bool Sort()
    {
        Check(observed!.Hidden.Values.All(slot => !active[slot]), "残りの行が非表示のまま最終ソート");
        sorts++;
        return false;
    }

    private static T Empty<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { assertions++; return; }
        throw new InvalidOperationException("不適合なILを拒否しませんでした。");
    }
}
