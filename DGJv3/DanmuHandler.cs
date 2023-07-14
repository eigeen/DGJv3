using BilibiliDM_PluginFramework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DGJv3
{
    class DanmuHandler : INotifyPropertyChanged
    {
        private ObservableCollection<SongItem> Songs;

        private ObservableCollection<BlackListItem> Blacklist;

        private Player Player;

        private Downloader Downloader;

        private SearchModules SearchModules;

        private Dispatcher dispatcher;

        private Dictionary<string, SongItem> _userLastSong = new Dictionary<string, SongItem>();

        /// <summary>
        /// 最多点歌数量
        /// </summary>
        public uint MaxTotalSongNum
        {
            get => _maxTotalSongCount;
            set => SetField(ref _maxTotalSongCount, value);
        }

        private uint _maxTotalSongCount;

        /// <summary>
        /// 每个人最多点歌数量
        /// </summary>
        public uint MaxPersonSongNum
        {
            get => _maxPersonSongNum;
            set => SetField(ref _maxPersonSongNum, value);
        }

        private uint _maxPersonSongNum;

        public uint CoolDown
        {
            get => _coolDown;
            set => SetField(ref _coolDown, value);
        }

        private uint _coolDown;

        internal DanmuHandler(ObservableCollection<SongItem> songs,
            Player player, Downloader downloader, SearchModules searchModules,
            ObservableCollection<BlackListItem> blacklist)
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            Songs = songs;
            Player = player;
            Downloader = downloader;
            SearchModules = searchModules;
            Blacklist = blacklist;
        }


        /// <summary>
        /// 处理弹幕消息
        /// <para>
        /// 注：调用侧可能会在任意线程
        /// </para>
        /// </summary>
        /// <param name="danmakuModel"></param>
        internal void ProcessDanmu(DanmakuModel danmakuModel)
        {
            if (danmakuModel.MsgType != MsgTypeEnum.Comment || string.IsNullOrWhiteSpace(danmakuModel.CommentText))
                return;

            string[] commands = danmakuModel.CommentText.Split(SPLIT_CHAR, StringSplitOptions.RemoveEmptyEntries);
            string rest = string.Join(" ", commands.Skip(1));

            if (danmakuModel.isAdmin)
            {
                // 管理员命令
                switch (commands[0])
                {
                    case "切歌":
                    {
                        // Player.Next();

                        dispatcher.Invoke(() =>
                        {
                            if (Songs.Count > 0)
                            {
                                Songs[0].Remove(Songs, Downloader, Player);
                                Log("切歌成功！");
                            }
                        });

                        /*
                        if (commands.Length >= 2)
                        {
                            // TODO: 切指定序号的歌曲
                        }
                        */
                    }
                        return;
                    case "暂停":
                    case "暫停":
                    {
                        Player.Pause();
                    }
                        return;
                    case "播放":
                    {
                        Player.Play();
                    }
                        return;
                    case "音量":
                    {
                        if (commands.Length > 1
                            && int.TryParse(commands[1], out int volume100)
                            && volume100 >= 0
                            && volume100 <= 100)
                        {
                            Player.Volume = volume100 / 100f;
                        }
                    }
                        return;
                    default:
                        break;
                }
            }

            // 通用命令
            switch (commands[0])
            {
                case "点歌":
                case "點歌":
                {
                    DanmuAddSong(danmakuModel, rest);
                }
                    return;
                case "取消點歌":
                case "取消点歌":
                {
                    dispatcher.Invoke(() =>
                    {
                        SongItem songItem = Songs.LastOrDefault(x =>
                            x.UserName == danmakuModel.UserName && x.Status != SongStatus.Playing);
                        if (songItem == null) return;

                        songItem.Remove(Songs, Downloader, Player);

                        // 未在播放的歌曲移除最后点歌记录，刷新CD
                        // 已播放的歌曲不刷新
                        if (songItem.Status == SongStatus.Playing) return;

                        if (_userLastSong.ContainsKey(danmakuModel.UserName))
                        {
                            _userLastSong.Remove(danmakuModel.UserName);
                        }
                    });
                }
                    return;
                case "投票切歌":
                {
                    // TODO: 投票切歌
                }
                    return;
                default:
                    break;
            }
        }

        private void DanmuAddSong(DanmakuModel danmakuModel, string keyword)
        {
            if (dispatcher.Invoke(callback: () => CanAddSong(username: danmakuModel.UserName)))
            {
                SongInfo songInfo = null;

                if (SearchModules.PrimaryModule != SearchModules.NullModule)
                    songInfo = SearchModules.PrimaryModule.SafeSearch(keyword);

                if (songInfo == null)
                    if (SearchModules.SecondaryModule != SearchModules.NullModule)
                        songInfo = SearchModules.SecondaryModule.SafeSearch(keyword);

                if (songInfo == null)
                    return;

                if (songInfo.IsInBlacklist(Blacklist))
                {
                    Log($"歌曲{songInfo.Name}在黑名单中");
                    return;
                }

                Log($"点歌成功:{songInfo.Name}");
                dispatcher.Invoke(callback: () =>
                {
                    if (CanAddSong(danmakuModel.UserName) &&
                        !Songs.Any(x =>
                            x.SongId == songInfo.Id &&
                            x.Module.UniqueId == songInfo.Module.UniqueId)
                       )
                    {
                        var songItem = new SongItem(songInfo, danmakuModel.UserName);
                        Songs.Add(songItem);
                        _userLastSong[danmakuModel.UserName] = songItem;
                    }
                });
            }
        }

        /// <summary>
        /// 能否点歌
        /// <para>
        /// 注：调用侧需要在主线程上运行
        /// </para>
        /// </summary>
        /// <param name="username">点歌用户名</param>
        /// <returns></returns>
        private bool CanAddSong(string username)
        {
            if (Songs.Count >= MaxTotalSongNum)
            {
                return false;
            }

            if (Songs.Count(x => x.UserName == username) >= MaxPersonSongNum)
            {
                return false;
            }

            if (_coolDown != 0)
            {
                var now = DateTime.Now;
                if (_userLastSong.TryGetValue(username, out var userLastSong))
                {
                    var addTime = userLastSong.AddTime;
                    var expTime = addTime.AddMinutes(_coolDown);
                    if (expTime.CompareTo(now) > 0) // cd时间未到，拒绝点歌
                    {
                        Log($"{username} CD时间未结束，点歌失败");
                        return false;
                    }
                }
            }

            return true;
        }

        private readonly static char[] SPLIT_CHAR = { ' ' };

        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        public event LogEvent LogEvent;

        private void Log(string message, Exception exception = null) => LogEvent?.Invoke(this,
            new LogEventArgs() { Message = message, Exception = exception });
    }
}