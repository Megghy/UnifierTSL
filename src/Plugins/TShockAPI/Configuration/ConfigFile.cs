using UnifierTSL.Plugins;

namespace TShockAPI.Configuration
{
    public class ConfigFile<TSettings> where TSettings : class, new() {
        private readonly IPluginConfigHandle<TSettings> settingsHandle;
        private TSettings settings;
        public TSettings Settings => settings;

        /// <summary>配置文件绝对路径。</summary>
        public string FilePath => settingsHandle.FilePath;

        public event Action<ConfigFile<TSettings>>? OnConfigRead;
        public ConfigFile(IPluginConfigRegistrar configRegistrar, string fileNameWithoutExtension, Func<TSettings> defaultSettingFactory) {
            settingsHandle = configRegistrar
                .CreateConfigRegistration<TSettings>(fileNameWithoutExtension + ".json")
                .WithDefault(defaultSettingFactory)
                .TriggerReloadOnExternalChange(true)
                .Complete();
            settingsHandle.OnChangedAsync += OnSettingsChanged;
            settings = settingsHandle.Request();
        }

        /// <summary>
        /// 覆盖内存中的配置并落盘。handle.Overwrite 不会触发 OnChangedAsync，所以这里自己同步缓存。
        /// </summary>
        public void Overwrite(TSettings newSettings) {
            settingsHandle.Overwrite(newSettings);
            ApplyLoaded(newSettings);
        }

        /// <summary>从磁盘重新加载并通知订阅者。</summary>
        public void Reload() {
            settingsHandle.Reload();
            ApplyLoaded(settingsHandle.Request());
        }

        private ValueTask<bool> OnSettingsChanged(IPluginConfigHandle<TSettings> handle, TSettings? config) {
            if (config is null) {
                return new ValueTask<bool>(true);
            }
            ApplyLoaded(config);
            return new ValueTask<bool>(false);
        }

        private void ApplyLoaded(TSettings newSettings) {
            settings = newSettings;
            OnConfigRead?.Invoke(this);
        }
    }
}
