﻿using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteUtilitiesCommon;
using PluginsCommon;
using SplashScreen.Models;
using SplashScreen.ViewModels;
using SplashScreen.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SplashScreen
{
    public class SplashScreen : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DispatcherTimer timerCloseWindow;
        private string pluginInstallPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private Window currentSplashWindow;
        private bool? isMusicMutedBackup;
        private EventWaitHandle videoWaitHandle;
        private readonly DispatcherTimer timerWindowRemoveTopMost;
        private const string featureNameSplashScreenDisable = "[Splash Screen] Disable";
        private const string featureNameSkipSplashImage = "[Splash Screen] Skip splash image";
        private const string videoIntroName = "VideoIntro.mp4";
        private const string microVideoName = "VideoMicrotrailer.mp4";

        private SplashScreenSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("d8c4f435-2bd2-49d8-98f6-87b1d415934a");

        public SplashScreen(IPlayniteAPI api) : base(api)
        {
            settings = new SplashScreenSettingsViewModel(this, PlayniteApi, GetPluginUserDataPath());
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            videoWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
            timerCloseWindow = new DispatcherTimer();
            timerCloseWindow.Interval = TimeSpan.FromMilliseconds(60000);
            timerCloseWindow.Tick += (_, __) =>
            {
                timerCloseWindow.Stop();
                currentSplashWindow?.Close();
            };

            timerWindowRemoveTopMost = new DispatcherTimer();
            timerWindowRemoveTopMost.Interval = TimeSpan.FromMilliseconds(800);
            timerWindowRemoveTopMost.Tick += (_, __) =>
            {
                timerWindowRemoveTopMost.Stop();
                if (currentSplashWindow != null)
                {
                    currentSplashWindow.Topmost = false;
                }
            };
        }

        private void MuteBackgroundMusic()
        {
            if (PlayniteApi.ApplicationInfo.Mode != ApplicationMode.Fullscreen)
            {
                return;
            }

            if (PlayniteApi.ApplicationSettings.Fullscreen.IsMusicMuted == false)
            {
                PlayniteApi.ApplicationSettings.Fullscreen.IsMusicMuted = true;
                isMusicMutedBackup = false;
            }
        }

        private void RestoreBackgroundMusic()
        {
            if (PlayniteApi.ApplicationInfo.Mode != ApplicationMode.Fullscreen)
            {
                return;
            }

            if (isMusicMutedBackup != null && isMusicMutedBackup == false)
            {
                PlayniteApi.ApplicationSettings.Fullscreen.IsMusicMuted = false;
                isMusicMutedBackup = null;
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (currentSplashWindow != null)
            {
                currentSplashWindow.Topmost = false;
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var game = args.Game;
            var generalSplashSettings = GetGeneralSplashScreenSettings(game);
            var modeSplashSettings = PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop
                                ? generalSplashSettings.DesktopModeSettings : generalSplashSettings.FullscreenModeSettings;
            if (!modeSplashSettings.IsEnabled)
            {
                logger.Info($"Execution disabled for {PlayniteApi.ApplicationInfo.Mode} mode in settings");
                return;
            }

            // In case somebody starts another game or if splash screen was not closed before for some reason
            if (currentSplashWindow != null)
            {
                currentSplashWindow.Close();
                currentSplashWindow = null;
                RestoreBackgroundMusic();
            }

            currentSplashWindow = null;
            if (PlayniteUtilities.GetGameHasFeature(game, featureNameSplashScreenDisable, true))
            {
                logger.Info($"{game.Name} has splashscreen disable feature");
                return;
            }

            var splashImagePath = string.Empty;
            var logoPath = string.Empty;

            var showSplashImage = GetShowSplashImage(game, modeSplashSettings);
            if (showSplashImage)
            {
                var usingGlobalImage = false;
                if (generalSplashSettings.EnableCustomBackgroundImage && !generalSplashSettings.CustomBackgroundImage.IsNullOrEmpty())
                {
                    var globalSplashImagePath = Path.Combine(GetPluginUserDataPath(), "CustomBackgrounds", generalSplashSettings.CustomBackgroundImage);
                    if (FileSystem.FileExists(globalSplashImagePath))
                    {
                        splashImagePath = globalSplashImagePath;
                        usingGlobalImage = true;
                        if (generalSplashSettings.EnableLogoDisplayOnCustomBackground)
                        {
                            logoPath = GetSplashLogoPath(game, generalSplashSettings);
                        }
                    }
                }

                if (!usingGlobalImage)
                {
                    if (generalSplashSettings.UseBlackSplashscreen)
                    {
                        splashImagePath = Path.Combine(pluginInstallPath, "Images", "SplashScreenBlack.png");
                    }
                    else
                    {
                        splashImagePath = GetSplashImagePath(game);
                    }

                    if (generalSplashSettings.EnableLogoDisplay)
                    {
                        logoPath = GetSplashLogoPath(game, generalSplashSettings);
                    }
                }
            }

            if (modeSplashSettings.EnableVideos)
            {
                var videoPath = GetSplashVideoPath(game, modeSplashSettings);
                if (!videoPath.IsNullOrEmpty())
                {
                    CreateSplashVideoWindow(showSplashImage, videoPath, splashImagePath, logoPath, generalSplashSettings, modeSplashSettings);
                    return;
                }
            }
            
            if (showSplashImage)
            {
                CreateSplashImageWindow(splashImagePath, logoPath, generalSplashSettings, modeSplashSettings);
            }
        }

        private GeneralSplashSettings GetGeneralSplashScreenSettings(Game game)
        {
            var gameSettingsPath = Path.Combine(GetPluginUserDataPath(), $"{game.Id}.json");
            if (FileSystem.FileExists(gameSettingsPath))
            {
                var gameSplashSettings = Serialization.FromJsonFile<GameSplashSettings>(gameSettingsPath);
                if (gameSplashSettings.EnableGameSpecificSettings)
                {
                    return gameSplashSettings.GeneralSplashSettings;
                }
            }

            return settings.Settings.GeneralSplashSettings;
        }

        private bool GetShowSplashImage(Game game, ModeSplashSettings modeSplashSettings)
        {
            if (PlayniteUtilities.GetGameHasFeature(game, featureNameSkipSplashImage, true))
            {
                return false;
            }

            return modeSplashSettings.EnableBackgroundImage;
        }

        private void CreateSplashVideoWindow(bool showSplashImage, string videoPath, string splashImagePath, string logoPath, GeneralSplashSettings generalSplashSettings, ModeSplashSettings modeSplashSettings)
        {
            // Mutes Playnite Background music to make sure its not playing when video or splash screen image
            // is active and prevents music not stopping when game is already running
            MuteBackgroundMusic();
            timerCloseWindow.Stop();
            currentSplashWindow?.Close();

            var content = new SplashScreenVideo(videoPath);
            currentSplashWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowState = WindowState.Maximized,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Focusable = false,
                Content = content,
                // Window is set to topmost to make sure another window won't show over it
                Topmost = true
            };

            currentSplashWindow.Closed += SplashWindowClosed;
            content.VideoPlayer.MediaEnded += VideoPlayer_MediaEnded;
            content.VideoPlayer.MediaFailed += VideoPlayer_MediaFailed;

            currentSplashWindow.Show();

            // To wait until the video stops playing, a progress dialog is used
            // to make Playnite wait in a non locking way and without sleeping the whole
            // application
            videoWaitHandle.Reset();
            PlayniteApi.Dialogs.ActivateGlobalProgress((_) =>
            {
                videoWaitHandle.WaitOne();
                content.VideoPlayer.MediaEnded -= VideoPlayer_MediaEnded;
                content.VideoPlayer.MediaFailed -= VideoPlayer_MediaFailed;
                logger.Debug("videoWaitHandle.WaitOne() passed");
            }, new GlobalProgressOptions(string.Empty) { IsIndeterminate = false });

            if (showSplashImage)
            {
                currentSplashWindow.Content = new SplashScreenImage { DataContext = new SplashScreenImageViewModel(generalSplashSettings, splashImagePath, logoPath) };
                PlayniteApi.Dialogs.ActivateGlobalProgress((a) =>
                {
                    Thread.Sleep(3000);
                }, new GlobalProgressOptions(string.Empty) { IsIndeterminate = false });

                if (modeSplashSettings.CloseSplashscreenAutomatic)
                {
                    timerCloseWindow.Stop();
                    timerCloseWindow.Start();
                }
            }
            else
            {
                currentSplashWindow?.Close();
            }

            timerWindowRemoveTopMost.Stop();
            timerWindowRemoveTopMost.Start();
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            videoWaitHandle.Set();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            videoWaitHandle.Set();
        }

        private void CreateSplashImageWindow(string splashImagePath, string logoPath, GeneralSplashSettings generalSplashSettings, ModeSplashSettings modeSplashSettings)
        {
            // Mutes Playnite Background music to make sure its not playing when video or splash screen image
            // is active and prevents music not stopping when game is already running
            timerCloseWindow.Stop();

            currentSplashWindow?.Close();
            MuteBackgroundMusic();
            currentSplashWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowState = WindowState.Maximized,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Focusable = false,
                Content = new SplashScreenImage { DataContext = new SplashScreenImageViewModel(generalSplashSettings, splashImagePath, logoPath) },
                // Window is set to topmost to make sure another window won't show over it
                Topmost = true
            };

            currentSplashWindow.Closed += SplashWindowClosed;
            currentSplashWindow.Show();
            PlayniteApi.Dialogs.ActivateGlobalProgress((a) =>
            {
                Thread.Sleep(3000);
            }, new GlobalProgressOptions(string.Empty) { IsIndeterminate = false});

            if (modeSplashSettings.CloseSplashscreenAutomatic)
            {
                timerCloseWindow.Stop();
                timerCloseWindow.Start();
            }

            // The window topmost property is set to false after a small time to make sure
            // Playnite does not restore itself over the created window after the method ends
            // and it starts launching the game
            timerWindowRemoveTopMost.Stop();
            timerWindowRemoveTopMost.Start();
        }

        private void SplashWindowClosed(object sender, EventArgs e)
        {
            timerCloseWindow.Stop();
            videoWaitHandle.Set();
            currentSplashWindow.Closed -= SplashWindowClosed;
            currentSplashWindow = null;
        }

        private string GetSplashVideoPath(Game game, ModeSplashSettings modeSplashSettings)
        {
            var baseVideoPathTemplate = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "ExtraMetadata", "{0}", "{1}");
            
            var baseSplashVideo = string.Format(baseVideoPathTemplate, "games", game.Id.ToString());
            var splashVideo = Path.Combine(baseSplashVideo, videoIntroName);
            if (FileSystem.FileExists(splashVideo))
            {
                logger.Info(string.Format("Specific game video found in {0}", splashVideo));
                return LogAcquiredVideo("Game-specific", splashVideo);
            }

            if (modeSplashSettings.EnableMicroTrailerVideos)
            {
                splashVideo = Path.Combine(baseSplashVideo, microVideoName);
                if (FileSystem.FileExists(splashVideo))
                {
                    return LogAcquiredVideo("Micro trailer", splashVideo);
                }
            }

            var videoPathTemplate = Path.Combine(baseVideoPathTemplate, videoIntroName);
            if (game.Source != null)
            {
                splashVideo = string.Format(videoPathTemplate, "sources", game.Source.Id.ToString());
                if (FileSystem.FileExists(splashVideo))
                {
                    return LogAcquiredVideo($"Source '{game.Source.Name}'", splashVideo);
                }
            }

            var platform = game.Platforms?.FirstOrDefault();
            if (platform != null)
            {
                splashVideo = string.Format(videoPathTemplate, "platforms", platform.Id.ToString());
                if (FileSystem.FileExists(splashVideo))
                {
                    return LogAcquiredVideo($"Platform '{platform.Name}'", splashVideo);
                }
            }

            var mode = PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop ? "Desktop" : "Fullscreen";
            splashVideo = string.Format(videoPathTemplate, "playnite", mode);

            if (FileSystem.FileExists(splashVideo))
            {
                return LogAcquiredVideo(mode, splashVideo);
            }
            else
            {
                logger.Info("Video not found");
                return string.Empty;
            }
        }

        private static string LogAcquiredVideo(string source, string videoPath)
        {
            logger.Info($"{source} video found in {videoPath}");
            return videoPath;
        }

        private string GetSplashLogoPath(Game game, GeneralSplashSettings generalSplashSettings)
        {
            if (generalSplashSettings.LogoUseIconAsLogo && game.Icon != null && !game.Icon.StartsWith("http"))
            {
                logger.Info("Found game icon");
                return PlayniteApi.Database.GetFullFilePath(game.Icon);
            }
            else
            {
                var logoPathSearch = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "ExtraMetadata", "games", game.Id.ToString(), "Logo.png");
                if (FileSystem.FileExists(logoPathSearch))
                {
                    logger.Info(string.Format("Specific game logo found in {0}", logoPathSearch));
                    return logoPathSearch;
                }
            }

            logger.Info("Logo not found");
            return string.Empty;
        }

        private string GetSplashImagePath(Game game)
        {
            if (game.BackgroundImage != null && !game.BackgroundImage.StartsWith("http"))
            {
                logger.Info("Found game background image");
                return PlayniteApi.Database.GetFullFilePath(game.BackgroundImage);
            }

            if (game.Platforms.HasItems() && game.Platforms[0].Background != null)
            {
                logger.Info("Found platform background image");
                return PlayniteApi.Database.GetFullFilePath(game.Platforms[0].Background);
            }

            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                logger.Info("Using generic Desktop mode background image");
                return Path.Combine(pluginInstallPath, "Images", "SplashScreenDesktopMode.png");
            }
            else
            {
                logger.Info("Using generic Fullscreen mode background image");
                return Path.Combine(pluginInstallPath, "Images", "SplashScreenFullscreenMode.png");
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            RestoreBackgroundMusic();
            // Close splash screen manually it was not closed automatically
            if (currentSplashWindow != null)
            {
                currentSplashWindow.Close();
                currentSplashWindow = null;
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SplashScreenSettingsView();
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSplashScreen_MenuItemInvoke-OpenVideoManagerWindowDescription"),
                    MenuSection = "@Splash Screen",
                    Action = a => {
                        OpenVideoManager();
                    }
                },
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSplashScreen_MenuItemAdd-DisableFeature"),
                    MenuSection = "@Splash Screen",
                    Action = a => {
                        PlayniteUtilities.AddFeatureToGames(PlayniteApi, PlayniteApi.MainView.SelectedGames.Distinct(), featureNameSplashScreenDisable);
                    }
                },
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSplashScreen_MenuItemRemove-DisableFeature"),
                    MenuSection = "@Splash Screen",
                    Action = a => {
                        PlayniteUtilities.RemoveFeatureFromGames(PlayniteApi, PlayniteApi.MainView.SelectedGames.Distinct(), featureNameSplashScreenDisable);
                    }
                },
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSplashScreen_MenuItemAdd-ImageSkipFeature"),
                    MenuSection = "@Splash Screen",
                    Action = a => {
                        PlayniteUtilities.AddFeatureToGames(PlayniteApi, PlayniteApi.MainView.SelectedGames.Distinct(), featureNameSkipSplashImage);
                    }
                },
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSplashScreen_MenuItemRemove-ImageSkipFeature"),
                    MenuSection = "@Splash Screen",
                    Action = a => {
                        PlayniteUtilities.RemoveFeatureFromGames(PlayniteApi, PlayniteApi.MainView.SelectedGames.Distinct(), featureNameSkipSplashImage);
                    }
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            return new List<GameMenuItem>
            {
                new GameMenuItem
                {
                    Description = string.Format(ResourceProvider.GetString("LOCSplashScreen_MenuItemOpenGameSplashscreenConfigWindow"), args.Games.Last().Name),
                    MenuSection = $"Splash Screen",
                    Action = a =>
                    {
                        OpenSplashScreenGameConfigWindow(args.Games.Last());
                    }
                },
                new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSplashScreen_MenuItemDeleteGamesSettings"),
                    MenuSection = $"Splash Screen",
                    Action = a =>
                    {
                        DeleteGamesSettings(args.Games);
                    }
                }
            };
        }

        private void OpenSplashScreenGameConfigWindow(Game game)
        {
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = true
            });

            window.Height = 700;
            window.Width = 900;
            window.Title = "Splash Screen";

            window.Content = new GameSettingsWindow();
            window.DataContext = new GameSettingsWindowViewModel(PlayniteApi, GetPluginUserDataPath(), game);
            window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            window.ShowDialog();
        }

        private void DeleteGamesSettings(List<Game> games)
        {
            foreach (var game in games)
            {
                var gameSettingsPath = Path.Combine(GetPluginUserDataPath(), $"{game.Id}.json");
                if (FileSystem.FileExists(gameSettingsPath))
                {
                    var gameSplashSettings = Serialization.FromJsonFile<GameSplashSettings>(gameSettingsPath);
                    if (!gameSplashSettings.GeneralSplashSettings.CustomBackgroundImage.IsNullOrEmpty())
                    {
                        var customImage = Path.Combine(GetPluginUserDataPath(), "CustomBackgrounds", gameSplashSettings.GeneralSplashSettings.CustomBackgroundImage);
                        if (FileSystem.FileExists(customImage))
                        {
                            FileSystem.DeleteFileSafe(customImage);
                        }
                    }

                    FileSystem.DeleteFileSafe(gameSettingsPath);
                }
            }

            PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString("LOCSplashScreen_GameSettingsWindowSettingsSavedLabel"), "Splash Screen");
        }

        private void OpenVideoManager()
        {
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false
            });

            window.Height = 600;
            window.Width = 800;
            window.Title = $"Splash Screen - {ResourceProvider.GetString("LOCSplashScreen_VideoManagerTitle")}";
            window.Content = new VideoManager();
            window.DataContext = new VideoManagerViewModel(PlayniteApi);
            window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            window.ShowDialog();
        }

    }
}