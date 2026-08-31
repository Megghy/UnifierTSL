using Newtonsoft.Json.Linq;
using System.Collections.Immutable;
using UnifierTSL.Plugins;

namespace TShockAPI.Configuration
{
    public class ServerConfigFile<TSettings> where TSettings : class, new()
    {
        private static readonly JsonMergeSettings PatchMergeSettings = new() {
            MergeArrayHandling = MergeArrayHandling.Replace,
            MergeNullValueHandling = MergeNullValueHandling.Ignore
        };

        private TSettings defaultSetting;
        private readonly IPluginConfigHandle<TSettings> defaultSettingHandle;
        private readonly IPluginConfigHandle<Dictionary<string, JObject>> serverSpecificSettingHandle;
        private ImmutableDictionary<string, TSettings> cachedServerSettings;

        public TSettings GlobalSettings => defaultSetting;

        /// <summary>全局配置文件绝对路径（不含 per-server override）。</summary>
        public string FilePath => defaultSettingHandle.FilePath;

        public TSettings GetServerSettings(string serverName) {
            if (cachedServerSettings.TryGetValue(serverName, out TSettings? value)) {
                return value;
            }
            var setting = CloneSettings(defaultSetting);
            ImmutableInterlocked.TryAdd(ref cachedServerSettings, serverName, setting);
            return setting;
        }

        public void SaveToFile() {
            var globalSnapshot = CloneSettings(defaultSetting);
            var globalObject = ToJObject(globalSnapshot);

            defaultSettingHandle.Overwrite(globalSnapshot);
            serverSpecificSettingHandle.Overwrite(cachedServerSettings.ToDictionary(
                pair => pair.Key,
                pair => CreatePatch(pair.Value, globalObject)));
        }

        public event Action<ServerConfigFile<TSettings>>? OnConfigRead;

        /// <summary>
        /// 覆盖全局配置并落盘。handle.Overwrite 不会触发 OnChangedAsync，所以这里自己同步缓存并通知订阅者。
        /// </summary>
        public void Overwrite(TSettings settings) {
            defaultSettingHandle.Overwrite(settings);
            ApplyLoaded(settings);
        }

        /// <summary>从磁盘重新加载全局配置，重建 per-server 缓存，并通知订阅者。</summary>
        public void Reload() {
            defaultSettingHandle.Reload();
            ApplyLoaded(defaultSettingHandle.Request()
                ?? throw new InvalidOperationException($"Unable to load default settings for {FilePath}"));
        }

        public ServerConfigFile(IPluginConfigRegistrar configRegistrar, string fileNameWithoutExtension) {

            var defaultFile = fileNameWithoutExtension + ".json";
            var serverSpecificFile = fileNameWithoutExtension + ".override.json";

            defaultSettingHandle = configRegistrar
                .CreateConfigRegistration<TSettings>(defaultFile, ConfigFormat.NewtonsoftJson)
                .WithDefault(static () => new TSettings())
                .TriggerReloadOnExternalChange(true)
                .Complete();
            defaultSettingHandle.OnChangedAsync += OnDefaultSettingChanged;

            serverSpecificSettingHandle = configRegistrar
                .CreateConfigRegistration<Dictionary<string, JObject>>(serverSpecificFile, ConfigFormat.NewtonsoftJson)
                .WithDefault(static () => new Dictionary<string, JObject>())
                .TriggerReloadOnExternalChange(true)
                .Complete();
            serverSpecificSettingHandle.OnChangedAsync += OnServerSettingChanged;

            defaultSetting = defaultSettingHandle.Request()
                ?? throw new Exception($"Unable to load default settings for {defaultFile}");
            cachedServerSettings = BuildServerSettings(serverSpecificSettingHandle.Request(), defaultSetting);
        }

        private ValueTask<bool> OnServerSettingChanged(IPluginConfigHandle<Dictionary<string, JObject>> handle, Dictionary<string, JObject>? serverSettings) {
            if (serverSettings is null) {
                return new ValueTask<bool>(true);
            }
            try {
                cachedServerSettings = BuildServerSettings(serverSettings, defaultSetting);
            }
            catch {
                return new ValueTask<bool>(true);
            }

            OnConfigRead?.Invoke(this);
            return new ValueTask<bool>(false);
        }

        private ValueTask<bool> OnDefaultSettingChanged(IPluginConfigHandle<TSettings> handle, TSettings? config) {
            if (config is null) {
                return new ValueTask<bool>(true);
            }

            ApplyLoaded(config);
            return new ValueTask<bool>(false);
        }

        private void ApplyLoaded(TSettings settings) {
            defaultSetting = settings;
            cachedServerSettings = BuildServerSettings(serverSpecificSettingHandle.Request(), defaultSetting);
            OnConfigRead?.Invoke(this);
        }

        private static ImmutableDictionary<string, TSettings> BuildServerSettings(
            IReadOnlyDictionary<string, JObject> serverSettings,
            TSettings globalSettings) {

            if (serverSettings.Count == 0) {
                return ImmutableDictionary<string, TSettings>.Empty;
            }

            var builder = ImmutableDictionary.CreateBuilder<string, TSettings>();
            foreach (var overrides in serverSettings) {
                builder.Add(overrides.Key, ApplyPatch(globalSettings, overrides.Value));
            }
            return builder.ToImmutable();
        }

        private static TSettings CloneSettings(TSettings settings) {
            return ToJObject(settings).ToObject<TSettings>()
                ?? throw new InvalidOperationException($"Unable to clone settings for {typeof(TSettings).Name}.");
        }

        private static JObject ToJObject(TSettings settings) {
            return JObject.FromObject(settings);
        }

        private static TSettings ApplyPatch(TSettings globalSettings, JObject patch) {
            var merged = ToJObject(globalSettings);
            merged.Merge(new JObject(patch), PatchMergeSettings);
            return merged.ToObject<TSettings>()
                ?? throw new InvalidOperationException($"Unable to apply server patch for {typeof(TSettings).Name}.");
        }

        private static JObject CreatePatch(TSettings serverSettings, JObject globalSettings) {
            var patch = ToJObject(serverSettings);
            foreach (var property in patch.Properties().ToArray()) {
                if (!globalSettings.TryGetValue(property.Name, out var globalValue)) {
                    continue;
                }

                if (JToken.DeepEquals(property.Value, globalValue)) {
                    property.Remove();
                }
            }
            return patch;
        }
    }
}
