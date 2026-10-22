namespace AnimeStudio.Application.ZzzText;

public enum ZzzTextLanguage
{
    Unknown = 0,
    SimplifiedChinese,
    TraditionalChinese,
    Japanese,
}

/// <summary>
/// ZZZ never writes the language anywhere — the same feature table exists once per language
/// under a different asset id, so the language has to be inferred from the script itself.
///
/// Two levels are used together:
/// <list type="bullet">
/// <item><see cref="ClassifyString"/> classifies one string when it contains script-specific
/// characters (kana, or a character that only exists in simplified/traditional Chinese).</item>
/// <item><see cref="ClassifyPayload"/> takes a majority vote over the strings that could be
/// classified, which anchors the neutral strings ("攻击力") to the payload's language and
/// rejects payloads that are not localisation tables at all.</item>
/// </list>
/// </summary>
public static class ZzzTextLanguageDetector
{
    private static readonly HashSet<char> SimplifiedOnly = new(
        "权该设观说时换来对经过还为个门问题真实发现认识应当语让义务经济级战略领导职业军事" +
        "机构执行为结构关系简单复杂变化这里那边会开关头电脑钟表风险转换选择备份载视频内容" +
        "权限冲突击伤害攻防速度获取奖励活动任务角色皮肤装备道具商店价格购买升级经验奖励敌" +
        "人战斗技能效果属性生命值能量冷却时间回合胜利失败开始结束继续返回确认取消设置语言" +
        "网络连接中断请求超时下载异常重试错误成功提示警告消息记录数据文件保存删除创建");

    private static readonly HashSet<char> TraditionalOnly = new(
        "權該設觀說時換來對經過還為個門問題真實發現認識應當語讓義務經濟級戰略領導職業軍事" +
        "機構執行為結構關係簡單複雜變化這裡那邊會開關頭電腦鐘錶風險轉換選擇備份載視頻內容" +
        "權限衝突擊傷害攻防速度獲取獎勵活動任務角色皮膚裝備道具商店價格購買升級經驗獎勵敵" +
        "人戰鬥技能效果屬性生命值能量冷卻時間回合勝利失敗開始結束繼續返回確認取消設置語言" +
        "網路連線中斷請求逾時下載異常重試錯誤成功提示警告訊息記錄資料檔案儲存刪除建立");

    public static bool ContainsCjk(string text)
    {
        foreach (char ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff')
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsKana(string text)
    {
        foreach (char ch in text)
        {
            if (ch >= '\u3040' && ch <= '\u30ff')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Classifies a single string; <see cref="ZzzTextLanguage.Unknown"/> means "no evidence".</summary>
    public static ZzzTextLanguage ClassifyString(string text)
    {
        int simplified = 0;
        int traditional = 0;
        int cjk = 0;
        bool kana = false;

        foreach (char ch in text)
        {
            if (ch >= '\u3040' && ch <= '\u30ff')
            {
                kana = true;
            }
            else if (ch >= '\u4e00' && ch <= '\u9fff')
            {
                cjk++;
                if (SimplifiedOnly.Contains(ch))
                {
                    simplified++;
                }
                else if (TraditionalOnly.Contains(ch))
                {
                    traditional++;
                }
            }
        }

        if (kana)
        {
            return ZzzTextLanguage.Japanese;
        }

        if (cjk == 0)
        {
            return ZzzTextLanguage.Unknown;
        }

        if (simplified > traditional)
        {
            return ZzzTextLanguage.SimplifiedChinese;
        }

        if (traditional > simplified)
        {
            return ZzzTextLanguage.TraditionalChinese;
        }

        return ZzzTextLanguage.Unknown;
    }

    /// <summary>Majority vote over the strings that <see cref="ClassifyString"/> could classify.</summary>
    public static ZzzTextLanguage ClassifyPayload(IReadOnlyList<ZzzTextLanguage> votes)
    {
        int simplified = 0;
        int traditional = 0;
        int japanese = 0;

        foreach (ZzzTextLanguage vote in votes)
        {
            switch (vote)
            {
                case ZzzTextLanguage.SimplifiedChinese:
                    simplified++;
                    break;
                case ZzzTextLanguage.TraditionalChinese:
                    traditional++;
                    break;
                case ZzzTextLanguage.Japanese:
                    japanese++;
                    break;
            }
        }

        if (japanese > simplified && japanese > traditional)
        {
            return ZzzTextLanguage.Japanese;
        }

        if (simplified > traditional)
        {
            return ZzzTextLanguage.SimplifiedChinese;
        }

        if (traditional > simplified)
        {
            return ZzzTextLanguage.TraditionalChinese;
        }

        return ZzzTextLanguage.Unknown;
    }

    public static string ToLocale(ZzzTextLanguage language) => language switch
    {
        ZzzTextLanguage.SimplifiedChinese => "zh-CN",
        ZzzTextLanguage.TraditionalChinese => "zh-TW",
        ZzzTextLanguage.Japanese => "ja",
        _ => "unknown",
    };
}
