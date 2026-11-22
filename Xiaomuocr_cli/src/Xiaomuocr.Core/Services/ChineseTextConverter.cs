using System.Runtime.InteropServices;
using System.Text;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 繁简互转。
/// Windows 使用系统 LCMapString（完整 Unicode 字表）；
/// 非 Windows / API 失败时回退到常用字字典。
/// </summary>
public static class ChineseTextConverter
{
    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;
    private const int LOCALE_ZH_CN = 0x0804; // 中文(简体，中国)
    private const int LOCALE_ZH_TW = 0x0404; // 中文(繁体，台湾)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringW(
        int locale,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[]? lpDestStr,
        int cchDest);

    /// <summary>将繁体中文转为简体。空串原样返回。</summary>
    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                int needed = LCMapStringW(
                    LOCALE_ZH_CN, LCMAP_SIMPLIFIED_CHINESE,
                    text, text.Length, null, 0);
                if (needed <= 0)
                    needed = Math.Max(text.Length * 2, 16);

                var dest = new char[needed];
                int written = LCMapStringW(
                    LOCALE_ZH_CN, LCMAP_SIMPLIFIED_CHINESE,
                    text, text.Length, dest, dest.Length);
                if (written > 0)
                    result = new string(dest, 0, written);
            }
            catch
            {
                // keep result = text, fall through to dictionary
            }
        }

        // 字典再扫一遍：补 LCMapString 偶发漏转的字（如 後→后、於→于）
        return FallbackToSimplified(result);
    }

    /// <summary>将简体中文转为繁体。空串原样返回。</summary>
    public static string ToTraditional(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                int needed = LCMapStringW(
                    LOCALE_ZH_TW, LCMAP_TRADITIONAL_CHINESE,
                    text, text.Length, null, 0);
                if (needed <= 0)
                    needed = Math.Max(text.Length * 2, 16);

                var dest = new char[needed];
                int written = LCMapStringW(
                    LOCALE_ZH_TW, LCMAP_TRADITIONAL_CHINESE,
                    text, text.Length, dest, dest.Length);
                if (written > 0)
                    result = new string(dest, 0, written);
            }
            catch
            {
                // keep result
            }
        }

        return FallbackToTraditional(result);
    }

    private static string FallbackToSimplified(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(FallbackMap.TryGetValue(c, out var s) ? s : c);
        return sb.ToString();
    }

    private static string FallbackToTraditional(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(FallbackReverseMap.TryGetValue(c, out var t) ? t : c);
        return sb.ToString();
    }

    private static readonly Dictionary<char, char> FallbackMap = BuildFallbackMap();
    private static readonly Dictionary<char, char> FallbackReverseMap = BuildReverseMap(FallbackMap);

    private static Dictionary<char, char> BuildReverseMap(Dictionary<char, char> forward)
    {
        var rev = new Dictionary<char, char>(forward.Count);
        foreach (var (t, s) in forward)
            rev.TryAdd(s, t); // 一对多时保留首次
        return rev;
    }

    private static Dictionary<char, char> BuildFallbackMap()
    {
        // 成对字符：繁、简、繁、简…
        const string pairs =
            "裡里裏里後后面麵體体發发雲云氣气東东書书車车馬马開开門门飛飞風风龍龙國国" +
            "華华為为爲为會会學学習习進进動动變变過过說說説说見见長长時时間间關关頭头" +
            "實实無无來來對对個个們们萬万業业與与從从這这還还麼么樣样點点種種據据當当" +
            "應应該该經经現现场場處处聲声總总產产衆众眾众務务質质術术區区縣县廣广慶庆" +
            "廳厅廠厂專专屬属層层歷历曆历審审寫写讀读認认識识議议記记許许論论语語話话" +
            "調调請请讓让證证護护視视觀观覺觉覽览親親類类顯显題题頁页項项順顺須须預预" +
            "領领額额餘余館馆驗验優优傳传僅僅價价億亿兒儿兩两冊册則则剛剛劍剑勝胜勞劳" +
            "勢势勵励勸劝協协卻却厭厌厲厉參参叢丛嚴严圖图圍围園园圓圆團团執执堅坚塵尘" +
            "墊垫墜坠墳坟墻墙牆墙墾垦壇坛壓压壘垒壞坏壟垄壯壮壺壶壽寿夠够夢梦奪夺奮奋" +
            "媽妈嬌娇孫孙宮宫寧宁寬宽寵宠寶寶將将尋寻導导屆届屍尸屢屡島岛峽峡崗岗嵐岚" +
            "嶺岭嶼屿嶽岳帥帅師师帳帐帶带幀帧幣币幫帮幹干幾几庫库廂厢廈厦廚厨廟庙廢废" +
            "廬庐張张強强彈弹彌弥彎弯彙汇彥彦徑径徠徕復复徵征徹彻恆恒恥耻悅悦悵怅悶闷" +
            "懲惩憊惫憐怜憑凭憚惮憤愤憫悯憲宪憶忆懇恳懶懒懷怀懸悬懺忏懼惧戀恋戲戏戶户" +
            "拋抛挾挟捨舍掃扫掙挣掛挂採采揀拣揚扬換换揮挥損损搖摇搗捣搶抢摟搂摯挚摳抠" +
            "撐撑撓挠撥拨撫抚撲扑撻挞撿捡擁拥擄掳擇择擊击擋挡擔担擠挤擬拟擯摈擰拧擱搁" +
            "擲掷擴扩擺摆擾扰攏拢攔拦攙搀攜携攝摄攢攒攤摊攪搅攬揽敗败敵敌數数斂敛斃毙" +
            "斬斩斷断於于晉晋晝昼暈晕暢畅暫暂曉晓曠旷曬晒櫃柜權权歐欧歲岁歸归殲歼殼壳" +
            "毀毁毆殴氈毡決决況况涼凉淚泪淨净淪沦淵渊淺浅渙涣減减渦涡測测渾浑湊凑湯汤" +
            "準准溝沟溫温滄沧滅灭滌涤滬沪滯滞滲渗滾滚滿满漁渔漢汉漣涟漬渍漲涨漸漸漿浆" +
            "潑泼潔洁潛潜潤润潰溃澀涩澆浇澇涝澗涧澤泽澱淀濁浊濃浓濕湿濘泞濟济濤涛濫滥" +
            "濱滨濺溅濾滤瀆渎瀉泻瀏浏瀕濒瀝沥瀟潇瀨濑瀾澜灑洒灘滩灣湾災灾烏乌煉炼煙烟" +
            "煥焕煩烦煬炀熒荧熱热熾炽燈灯燉炖燒烧燙烫營营燦灿燭烛燴烩燼烬爍烁爐炉爛烂" +
            "爭争爺爷爾尔牘牍牽牵犢犊犧牺狀状狹狭狽狈猙狰猶犹獄狱獅狮獎奖獨独獰狞獲获" +
            "獵猎獷犷獸兽獺獭獻献琺珐瑣琐瑤瑶瑩莹瑪玛環环璽玺瓊琼瓏珑甌瓯畝亩畢毕異异" +
            "疇畴痙痉瘋疯瘍疡瘓痪瘡疮瘧疟療疗癆痨癇痫癒愈癘疠癟瘪癡痴癢痒癤疖癥症癩癞" +
            "癬癣癮瘾癰痈癱瘫癲癫皚皑皰疱皺皱盃杯盜盗盞盏盡尽監监盤盘盧卢盪荡睜睁睞睐" +
            "瞞瞒瞼睑矚瞩矯矫硃朱硯砚碩硕確确碼码磚砖礎础礙碍礦矿礪砺礫砾礬矾祿禄禍祸" +
            "禎祯禪禅禮礼禰祢禱祷禿秃稅税稈秆稱称穀谷穌稣積积穎颖穢秽穩稳窩窝窪洼窮穷" +
            "窯窑窺窥竄窜竅窍竇窦竈灶竊窃竪竖競竞筍笋節节範范築筑篤笃篩筛簍篓簡简簫箫" +
            "簽签簾帘籃篮籌筹籠笼籬篱籮箩粵粤糞粪糧粮糾纠紀纪約约紅红紉纫紋纹納纳紐纽" +
            "純纯紗纱綱纲網网綜综綠绿綽绰綫线線线緞缎緩缓締缔編编緣缘縛缚縫缝縮缩縱纵" +
            "縷缕績绩繃绷繆缪織织繕缮繚缭繞绕繡绣繩绳繪绘繫系繭茧繳缴繹绎繼继繽缤續续" +
            "纏缠纓缨纔才纖纤纜缆缽钵罰罚罵骂罷罢羅罗羈羁義义翹翘聖圣聞闻聯联聰聪聳耸" +
            "聶聂職职聽聽聾聋肅肃脅胁脈脉脫脱脹胀腎肾腦脑腫肿腳脚腸肠臥卧臨临興兴舉举" +
            "舊旧艙舱艦舰艱艰艷艳芻刍莖茎莊庄莢荚菴庵葦苇葯药蔥葱蓋盖蓮莲蔔卜蔣蒋蔭荫" +
            "蕎荞蕩荡蕪芜蕭萧薔蔷薊蓟薑姜薦荐薩萨薺荠藍蓝藝艺藥药藪薮藹蔼藺蔺蘆芦蘇苏" +
            "蘊蕴蘋苹蘚藓蘭兰蘿萝虛虚虜虏號号虧亏蟲虫蛻蜕蝕蚀蝦虾蝸蜗螞蚂螢萤螻蝼蟄蛰" +
            "蟬蝉蟻蚁蠅蝇蠟蜡蠱蛊蠶蚕蠻蛮衛卫衝冲袞衮補补裝装製制複复褲裤襖袄襪袜襯衬" +
            "襲袭規规覓觅覘觇覬觊覲觐覷觑覿觌觴觞觸触訂订計计訊讯討讨訓训訖讫託托訛讹" +
            "訝讶訟讼訣诀訥讷訪访設设訴诉訶诃診诊註注詁诂詆诋詐诈詔诏評评詛诅詞词詠咏" +
            "詢询詣诣試試詩诗詫诧詬诟詭诡詮诠詰诘詳详詼诙誅诛誇夸誌志誕诞誘诱誚诮誠诚" +
            "誡诫誣诬誤误誥诰誦诵誨诲誰谁課课誹诽誼谊諂谄諄谆談談諉诿諍诤諒谅諛谀諜谍" +
            "諞谝諤谔諦谛諧谐諫谏諭谕諮咨諱讳諳谙諷讽諸诸諺谚諾诺謀谋謁谒謂谓謄誊謅诌" +
            "謊谎謎谜謐谧謔谑謗谤謙谦講讲謝謝謠谣謨谟謫谪謬谬謳讴謹谨謾谩譎谲譏讥譖谮" +
            "譙谯譚谭譜谱譫谵譯译譴谴譽誉讀读讒谗讖谶讚赞豈岂豐丰豬猪貓猫貝贝貞贞負负" +
            "財财貢贡貧贫貨货販贩貪贪貫贯責责貯贮貰贳貳贰貴贵貶贬買买貸贷費费貼贴貽贻" +
            "貿贸賀贺賂赂賃赁賄贿資资賈贾賊贼賑赈賒赊賓宾賜赐賞赏賠赔賢贤賣卖賤贱賦赋" +
            "賬账賭赌賴赖賺赚購购賽赛贅赘贈赠贊赞贍赡贏赢贓赃贖赎贛赣趕赶趙赵趨趋跡迹" +
            "踐践踴踊蹤踪蹺跷躉趸躋跻躍跃躑踯躓踬躡蹑躥蹿軀躯軋轧軌轨軍军軒轩軟软軸轴" +
            "軻轲軼轶軾轼較较載载輒辄輔辅輕輕輛辆輝辉輟辍輩辈輪轮輯辑輸输輻辐輾辗輿舆" +
            "轄辖轅辕轆辘轉轉轍辙轎轿轟轰辦办辭辞辮辫辯辩農农迴回逕径連连週周運运達达" +
            "違违遙遥遜逊遞递遠遠適适遲迟遷迁選选遺遗遼辽邁迈邇迩邊边邏逻鄧邓醜丑醞酝" +
            "醫医醬酱釀酿釁衅釋释釘钉針针釣钓釵钗鈣钙鈦钛鈍钝鈔钞鈉钠鋁铝銅铜銑铣銘铭" +
            "銜衔銬铐銳锐銷销銹锈銼锉鋅锌鋒锋鋤锄鋪铺鍊炼鍋锅鍍镀鍛锻鍥锲鍬锹鍵键鍾钟" +
            "鎂镁鎊镑鎖锁鎗枪錘锤錨锚錫锡錮锢錯错錳锰錶表鍘铡鎧铠鎬镐鎮镇鎳镍鏃镞鏈链" +
            "鏗铿鏘锵鏜镗鏟铲鏡镜鏢镖鏤镂鏨錾鐃铙鐐镣鐒铹鐙镫鐫镌鐮镰鐲镯鐳镭鐵铁鐸铎" +
            "鐺铛鑄铸鑊镬鑑鉴鑒鉴鑛矿鑠铄鑣镳鑰钥鑲镶鑷镊鑼锣鑽钻鑾銮鑿凿閂闩閃闪閉闭" +
            "閏闰閑闲閒闲閔闵閘闸閡阂閣阁閥阀閨闺閩闽閭闾閱阅閹阉閻阎闆板闊阔闌阑闔阖" +
            "闕阙闖闯闡阐闢辟陣阵陰阴陳陈陸陆陽阳隊队階阶隕陨際际隨随險险隱隐隴陇隸隶" +
            "隻只雋隽雖虽雙双雛雏雜杂雞鸡離离難难電电霧雾霽霁靂雳靄霭靈灵靜静鞏巩鞦秋" +
            "韃鞑韋韦韌韧韓韩韜韬韻韵響响頂顶頃顷頌颂頑顽頒颁頓顿頗颇頡颉頤颐頰颊頸颈" +
            "頹颓頻频顆颗顎颚顏颜願愿顛颠顧顾顫颤顰颦顱颅颯飒颱台颳刮颶飓颼飕飄飘飆飙" +
            "飢饥飯饭飲饮飾饰飽饱飼饲餃饺餅饼餉饷養养餌饵餒馁餓饿餛馄餞饯餡馅餵喂餾馏" +
            "餿馊饅馒饉馑饋馈饌馔饑饥饒饶饗飨饜餍饞馋馭驭馮冯馱驮馳驰馴驯駁驳駐驻駕驾" +
            "駛驶駝驼駟驷駭骇駱骆駿骏騁骋騎骑騖骛騙骗騫骞騰腾騷骚騾骡驀蓦驅驱驍骁驕骄" +
            "驛驿驟骤驢驴驥骥驪骊髒脏鬆松鬍胡鬚须鬢鬓鬥斗鬧闹鬱郁魚鱼魯鲁鮑鲍鮮鲜鯉鲤" +
            "鯊鲨鯖鲭鯛鲷鯨鲸鯽鲫鰍鳅鰐鳄鰣鲥鰱鲢鱉鳖鰻鳗鱈鳕鱒鳟鱔鳝鱖鳜鱗鳞鱘鲟鱟鲎" +
            "鱧鳢鱭鲚鱸鲈鳩鸠鳶鸢鴆鸩鴇鸨鴉鸦鴕鸵鴛鸳鴻鸿鴿鸽鵑鹃鵝鹅鵠鹄鵡鹉鵲鹊鵪鹌" +
            "鵬鹏鶯莺鶴鹤鶿鹚鷂鹞鷄鸡鷓鹧鷗鸥鷙鸷鷥鸶鷹鹰鷺鹭鸚鹦鸛鹳鸞鸾鹵卤鹹咸鹽盐" +
            "麗丽麩麸黉黉黽黾黿鼋鼉鼍齋斋齡龄齣出齦龈齪龊齬龉齲龋齷龌龐庞龔龚龕龛";

        var map = new Dictionary<char, char>(pairs.Length / 2);
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            map.TryAdd(pairs[i], pairs[i + 1]);
        return map;
    }
}
