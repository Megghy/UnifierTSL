using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria.ID;
using TShockAPI.Hooks;
using TShockAPI.Localization;

namespace TShockAPI.DB
{
    public class ItemManager
    {
        [Table(Name = "ItemBansTable")]
        class ItemBansTable
        {
            [PrimaryKey]
            [Column(DataType = DataType.VarChar, Length = 50)]
            public required string Name { get; set; }
            [Column(DataType = DataType.Text)]
            public required string AllowedGroups { get; set; }
        }
        readonly Func<DataConnection> _dbFactory;
        readonly Lock _sync = new();
        public List<ItemBan> ItemBans = [];
        Dictionary<string, ItemBan> _byName = new(StringComparer.Ordinal);
        Dictionary<int, ItemBan> _byType = [];

        public bool HasBans => Volatile.Read(ref _byName).Count > 0;

        public ItemManager(DataConnection db) {
            _dbFactory = DataConnectionFactory.FromPrototype(db);

            using (var localDb = _dbFactory()) {
                localDb.CreateTable<ItemBansTable>(tableOptions: TableOptions.CreateIfNotExists);
            }

            UpdateItemBans();
        }
        public void UpdateItemBans() {
            try {
                using var db = _dbFactory();
                var bans = db.GetTable<ItemBansTable>()
                    .Select(b => new { b.Name, b.AllowedGroups })
                    .ToList();

                var loaded = new List<ItemBan>(bans.Count);
                foreach (var b in bans) {
                    ItemBan ban = new ItemBan(b.Name);
                    ban.SetAllowedGroups(b.AllowedGroups ?? "");
                    loaded.Add(ban);
                }

                lock (_sync) {
                    PublishSnapshot(loaded);
                }
            }
            catch (Exception ex) {
                TShock.Log.Error(ex.ToString());
            }
        }

        public void AddNewBan(string itemname = "") {
            try {
                using var db = _dbFactory();
                var itemBansTable = db.GetTable<ItemBansTable>();
                var existing = itemBansTable.Where(b => b.Name == itemname).FirstOrDefault();
                if (existing == null) {
                    itemBansTable.Insert(() => new ItemBansTable {
                        Name = itemname,
                        AllowedGroups = ""
                    });
                }

                lock (_sync) {
                    if (!ItemIsBanned(itemname, null)) {
                        var next = new List<ItemBan>(ItemBans.Count + 1);
                        next.AddRange(ItemBans);
                        next.Add(new ItemBan(itemname));
                        PublishSnapshot(next);
                    }
                }
            }
            catch (Exception ex) {
                TShock.Log.Error(ex.ToString());
            }
        }

        public void RemoveBan(string itemname) {
            if (!ItemIsBanned(itemname, null))
                return;

            try {
                using var db = _dbFactory();
                var itemBansTable = db.GetTable<ItemBansTable>();
                itemBansTable.Where(b => b.Name == itemname).Delete();

                lock (_sync) {
                    var next = new List<ItemBan>(ItemBans);
                    if (next.Remove(new ItemBan(itemname))) {
                        PublishSnapshot(next);
                    }
                }
            }
            catch (Exception ex) {
                TShock.Log.Error(ex.ToString());
            }
        }

        public bool AllowGroup(string item, string name) {
            var b = GetItemBanByName(item);
            if (b != null) {
                try {
                    using var db = _dbFactory();
                    var itemBansTable = db.GetTable<ItemBansTable>();
                    string groupsNew = string.Join(",", b.AllowedGroups);
                    if (groupsNew.Length > 0)
                        groupsNew += ",";
                    groupsNew += name;
                    b.SetAllowedGroups(groupsNew);

                    int q = itemBansTable
                        .Where(x => x.Name == item)
                        .Set(x => x.AllowedGroups, groupsNew)
                        .Update();

                    return q > 0;
                }
                catch (Exception ex) {
                    TShock.Log.Error(ex.ToString());
                }
            }

            return false;
        }

        public bool RemoveGroup(string item, string group) {
            var b = GetItemBanByName(item);
            if (b != null) {
                try {
                    using var db = _dbFactory();
                    var itemBansTable = db.GetTable<ItemBansTable>();
                    b.RemoveGroup(group);
                    string groups = string.Join(",", b.AllowedGroups);

                    int q = itemBansTable
                        .Where(x => x.Name == item)
                        .Set(x => x.AllowedGroups, groups)
                        .Update();

                    if (q > 0)
                        return true;
                }
                catch (Exception ex) {
                    TShock.Log.Error(ex.ToString());
                }
            }
            return false;
        }

        public bool ItemIsBanned(string? name) => name is not null && Volatile.Read(ref _byName).ContainsKey(name);

        public bool ItemIsBanned(int type, TSPlayer? ply)
            => Volatile.Read(ref _byType).TryGetValue(type, out var ban) && !ban.HasPermissionToUseItem(ply);

        public bool ItemIsBanned(string? name, TSPlayer? ply) {
            if (name is null) return false;
            return Volatile.Read(ref _byName).TryGetValue(name, out var ban) && !ban.HasPermissionToUseItem(ply);
        }

        public ItemBan? GetItemBanByName(string? name)
            => name is not null && Volatile.Read(ref _byName).TryGetValue(name, out var ban) ? ban : null;

        internal static void FillLookups(
            List<ItemBan> bans,
            Dictionary<string, ItemBan> byName,
            Dictionary<int, ItemBan> byType,
            Func<int, string?> getName,
            int itemCount)
        {
            byName.Clear();
            byType.Clear();
            for (int i = 0; i < bans.Count; i++)
                byName[bans[i].Name] = bans[i];
            if (byName.Count == 0)
                return;
            for (int id = 1; id < itemCount; id++)
            {
                var name = getName(id);
                if (name is not null && byName.TryGetValue(name, out var ban))
                    byType[id] = ban;
            }
        }

        void PublishSnapshot(List<ItemBan> next)
        {
            var byName = new Dictionary<string, ItemBan>(next.Count, StringComparer.Ordinal);
            var byType = new Dictionary<int, ItemBan>();
            FillLookups(next, byName, byType, EnglishLanguage.GetItemNameById, ItemID.Count);
            Volatile.Write(ref _byName, byName);
            Volatile.Write(ref _byType, byType);
            Volatile.Write(ref ItemBans, next);
        }
    }
    public class ItemBan : IEquatable<ItemBan>
    {
        public string Name { get; set; }
        public List<string> AllowedGroups { get; set; }

        public ItemBan(string name)
            : this() {
            Name = name;
            AllowedGroups = new List<string>();
        }

        public ItemBan() {
            Name = "";
            AllowedGroups = new List<string>();
        }

        public bool Equals(ItemBan? other) {
            if (other == null) return false;
            return Name == other.Name;
        }

        public bool HasPermissionToUseItem(TSPlayer? ply) {
            if (ply == null)
                return false;

            if (ply.HasPermission(Permissions.usebanneditem))
                return true;

            PermissionHookResult hookResult = PlayerHooks.OnPlayerItembanPermission(ply, this);
            if (hookResult != PermissionHookResult.Unhandled)
                return hookResult == PermissionHookResult.Granted;

            var cur = ply.Group;
            var traversed = new List<Group>();
            while (cur != null) {
                if (AllowedGroups.Contains(cur.Name)) {
                    return true;
                }
                if (traversed.Contains(cur)) {
                    throw new InvalidOperationException("Infinite group parenting ({0})".SFormat(cur.Name));
                }
                traversed.Add(cur);
                cur = cur.Parent;
            }
            return false;
            // could add in the other permissions in this class instead of a giant if switch.
        }

        public void SetAllowedGroups(string groups) {
            // prevent null pointer exceptions
            if (!string.IsNullOrEmpty(groups)) {
                List<string> groupArr = groups.Split(',').ToList();

                for (int i = 0; i < groupArr.Count; i++) {
                    groupArr[i] = groupArr[i].Trim();
                    //Console.WriteLine(groupArr[i]);
                }
                AllowedGroups = groupArr;
            }
        }

        public bool RemoveGroup(string groupName) {
            return AllowedGroups.Remove(groupName);
        }

        public override string ToString() {
            return Name + (AllowedGroups.Count > 0 ? " (" + string.Join(",", AllowedGroups) + ")" : "");
        }
    }
}
