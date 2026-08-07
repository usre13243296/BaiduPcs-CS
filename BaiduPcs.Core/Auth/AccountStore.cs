using System.Text.Json;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Auth;

/// <summary>
/// 多账号本地存储（JSON 文件，原子写入）。
/// 对应 Go 端 pcsconfig 的账号列表部分，但独立成类且可注入路径以便测试。
/// </summary>
public sealed class AccountStore
{
    private readonly string _filePath;
    private readonly List<BaiduAccount> _accounts = new();

    /// <summary>当前账号的 UID；未登录时为 null。</summary>
    public long? ActiveUid { get; private set; }

    public AccountStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.AccountStorePath;
        Load();
    }

    public IReadOnlyList<BaiduAccount> Accounts => _accounts;

    public BaiduAccount? ActiveAccount =>
        ActiveUid is { } uid ? _accounts.FirstOrDefault(a => a.Uid == uid) : null;

    /// <summary>加载本地文件（不存在或解析失败时视为空列表）。</summary>
    private void Load()
    {
        _accounts.Clear();
        ActiveUid = null;

        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var store = JsonSerializer.Deserialize<AccountStoreFile>(File.ReadAllText(_filePath));
            if (store is null)
            {
                return;
            }

            _accounts.AddRange(store.Accounts ?? new List<BaiduAccount>());
            if (store.ActiveUid is { } uid && _accounts.Any(a => a.Uid == uid))
            {
                ActiveUid = uid;
            }
        }
        catch (JsonException)
        {
            // 配置文件损坏时忽略，等待重新登录
        }
        catch (IOException)
        {
            // 忽略 IO 错误
        }
    }

    /// <summary>新增或更新账号，并设为当前账号。</summary>
    public void Upsert(BaiduAccount account)
    {
        var index = _accounts.FindIndex(a => a.Uid == account.Uid);
        if (index >= 0)
        {
            _accounts[index] = account;
        }
        else
        {
            _accounts.Add(account);
        }

        ActiveUid = account.Uid;
        Save();
    }

    public void SetActive(long uid)
    {
        if (_accounts.Any(a => a.Uid == uid))
        {
            ActiveUid = uid;
            Save();
        }
    }

    public void Remove(long uid)
    {
        _accounts.RemoveAll(a => a.Uid == uid);
        if (ActiveUid == uid)
        {
            ActiveUid = _accounts.FirstOrDefault()?.Uid;
        }

        Save();
    }

    /// <summary>原子写入 JSON 文件。</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var store = new AccountStoreFile
        {
            Accounts = _accounts,
            ActiveUid = ActiveUid,
        };

        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed class AccountStoreFile
    {
        public List<BaiduAccount>? Accounts { get; set; }
        public long? ActiveUid { get; set; }
    }
}
