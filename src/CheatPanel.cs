/*
 * HTF Cheat (BepInEx 叠加层) — IMGUI 面板（2Take1Menu 风格 v2.0）
 * 深色半透明 + 横向页签 + 纵向树形子菜单 + 键盘导航（方向键/小键盘双映射）。
 * 菜单结构在 CheatMenuModel.cs 声明式构建，本文件只负责渲染与导航。
 * 自定义区块（金钱/生成浏览器/渔获/给饵/任务/玩家传送/皮肤机/手持编辑/诊断）
 * 保留原 GUILayout 鼠标驱动实现，键盘游标跳过。
 * 中文渲染：从系统加载微软雅黑动态字体注入 GUISkin。
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using FishNet;
using UnityEngine;

public class CheatPanel : MonoBehaviour
{
	private const int WindowId = 12;

	internal const string Version = "v2.0";

	// ---- 主题常量（2Take1Menu 风格，集中一处可调）----
	private static readonly Color C_Accent = new Color(0.25f, 0.85f, 1f);
	private static readonly Color C_BgWindow = new Color(0.05f, 0.05f, 0.08f, 1f);
	private static readonly Color C_BgSel = new Color(0.22f, 0.22f, 0.42f, 0.95f);
	private static readonly Color C_BgHover = new Color(1f, 1f, 1f, 0.06f);
	private static readonly Color C_Text = new Color(0.87f, 0.89f, 0.92f);
	private static readonly Color C_TextDim = new Color(0.55f, 0.58f, 0.62f);
	private static readonly Color C_Warn = new Color(1f, 0.76f, 0.3f);
	private static readonly Color C_Border = new Color(0.25f, 0.85f, 1f, 0.3f);
	private const float ROW_H = 26f;

	/// <summary>窗口高度自适应时，状态栏之下留的余白（边框 + 视觉呼吸）。</summary>
	private const float WINDOW_PAD = 10f;

	/// <summary>行高随字号缩放（字号 ×1 时 = 26）。</summary>
	private static int RowH => Mathf.RoundToInt(ROW_H * CheatState.FontScale);

	/// <summary>窗口基准宽度（字号 ×1 时 = 380）。宽度随字号等比放大，见 ApplyWindowWidth。</summary>
	private const float BASE_W = 380f;

	/// <summary>滚动区最小高度：内容不足时窗口也不会塌成空壳，同时足够小到不与窗口最小高打架。</summary>
	private const float MIN_SCROLL = 60f;

	private static CheatPanel _instance;

	/// <summary>面板是否打开（输入屏蔽补丁读这个：开着 → 本地玩家 BlockInputs 强制 true）。</summary>
	internal static bool IsOpen => (bool)_instance && _instance._open;

	/// <summary>自家文本框输入中（输入屏蔽补丁读这个：菜单开着但正在打搜索词时不锁输入）。</summary>
	internal static bool IsTyping => (bool)_instance && _instance._uiTyping;

	// 自家所有输入框的 controlName。焦点落在其中任何一个时，键盘导航/快捷键
	// 都必须让位给打字，否则输数字会被导航键吃掉（典型事故：小键盘 0 → 返回、
	// 7/9 → 切页签）。新增输入框务必登记到这里，别再散落到各处硬编码。
	private static readonly string[] InputControlNames = new string[6]
	{
		"mn_amount", "sp_search", "it_amount", "tm_loop", "tm_interval", "tm_money"
	};

	/// <summary>焦点是否落在自家输入框里（HandleKeyboardNav 与 _uiTyping 共用同一份判定）。</summary>
	private static bool IsOurInputFocused()
	{
		string focused = GUI.GetNameOfFocusedControl();
		if (string.IsNullOrEmpty(focused))
		{
			return false;
		}
		for (int i = 0; i < InputControlNames.Length; i++)
		{
			if (focused == InputControlNames[i])
			{
				return true;
			}
		}
		return false;
	}

	private Rect _windowRect = new Rect(20f, 60f, 380f, 470f);

	private bool _open;

	private Vector2 _scroll;

	private int _tab;

	// ---- 导航状态机 ----
	private CheatMenuPage[] _pages;
	private readonly List<List<CheatMenuEntry>> _pageStack = new List<List<CheatMenuEntry>>();
	private readonly List<string> _crumbStack = new List<string>();
	private int _selIndex;
	private Rect _scrollViewRect;
	private Rect _selRect;

	// 各菜单列表的记忆游标（selIndex, scroll.y）——进入子菜单/切页时存取
	private readonly Dictionary<List<CheatMenuEntry>, Vector2> _posMemory = new Dictionary<List<CheatMenuEntry>, Vector2>();

	// 高度自适应：内容区真实高度（OnGUI 里据此调窗口高）
	private float _contentH;

	// 视口顶（窗口内容坐标系，实测）：内容高与下方常显区都以它为基准。
	// 旧写法用硬编码 68+RowH 当 chrome，字号缩放/错误框出现时必然对不上。
	private float _viewTop;

	// 本帧滚动区请求高度（正推：内容高 or 窗口剩余空间，不再反过来喂窗口高）
	private float _scrollH = 320f;

	// 下方常显区预算（杀闪烁根因的关键字段，详见 UpdateBelowBudget 内的注释）
	private float _belowBudget = 150f;

	// ---- 下方常显区的"实测预算"状态 ----
	// 旧写法把状态行写死 Height(RowH)、错误框写死"每条 2 行"，预算是拍脑袋常量：
	// 状态文字一长就静默截断，错误一多窗口就算矮、状态栏被推出窗口外（H2）。
	// 现在改为在 Repaint 阶段按当帧文本用 CalcHeight 逐行实测，存进下面这几个数组，
	// 下一帧**同时**用于"绘制高度"和"窗口高计算"——两者取自同一次测量，永远相等，
	// 于是既不会互相追着变（不闪），也不会算矮（不裁）。
	private readonly float[] _statusH = new float[4] { 26f, 26f, 26f, 26f };

	private readonly string[] _statusText = new string[4] { "", "", "", "" };

	// 错误框：缓存预算时的错误文本，绘制时照抄——保证"画出来的行数 == 预算的行数"
	private readonly string[] _errText = new string[3] { "", "", "" };

	private readonly float[] _errH = new float[3];

	private int _errN;

	// 错误框整体预算高（错误行高和 + 按钮高 + 间距），Layout 阶段随快照一起算好；
	// UpdateBelowBudget 只把它累进 _belowBudget，不再重复读队列 ——
	// 否则"数量"只在 Repaint 更新、绘制却在 Layout/Repaint 两阶段都发生，
	// 错误入队的那一帧两阶段控件数差 1，直接抛 "Getting control X ... only X controls"。
	private float _errBoxH;

	// 快照时刻的错误总数（"复制全部异常 (N)" 按钮文案用，避免按钮宽度在两阶段间变）
	private int _errCount;

	private float _statusBarH = 24f;

	// 预算收小的延迟时间戳：长→短立刻生效（避免裁切），短→长延迟生效（避免在换行
	// 临界点上每帧抖动）。Boss 状态的离场 tick 每帧变化，没有这个阻尼会反复换行/不换行。
	private float _belowShrinkAt = -1f;

	// Cycle 行右侧档位区宽度缓存（按 Options 数组引用）：按当前档位测宽会让切档时右侧区忽宽忽窄
	private readonly Dictionary<string[], float> _cycleRightW = new Dictionary<string[], float>();

	private float _appliedFontScale = -1f;

	// 用户是否手动拖过窗口宽度（拖过即视为接管宽度，不再跟随字号自动改宽）
	private bool _manualWidth;

	private bool _prevAutoFit = true;

	// 滚动内容首行的 y：所有"内容坐标"都减去它换算。
	// 用差值而非绝对值，就不依赖 IMGUI 的坐标系约定，也不会把滚动偏移算进去。
	private float _contentTop = -1f;

	// F9 刚打开菜单时为真：OnGUI 里补一次 FocusWindow，免去"先点一下才能键盘操作"
	private bool _needsFocus;

	private readonly Queue<string> _errors = new Queue<string>();

	private readonly Dictionary<string, float> _pendingLog = new Dictionary<string, float>();

	private float _lastBackupPressTime = -10f;

	// 右下角拖拽缩放状态
	private bool _resizing;

	// 拖拽中累积的尺寸增量（GUILayout.Window 返回后统一落地，防被返回值覆盖）
	private Vector2 _resizeDelta;

	// 中文皮肤缓存 + 主题样式
	private static GUISkin _skin;
	private static GUIStyle _winStyle;
	private static GUIStyle _rowStyle;
	private static GUIStyle _rightStyle;
	private static GUIStyle _dimStyle;
	private static GUIStyle _titleStyle;
	private static GUIStyle _centerStyle;
	private static GUIStyle _errorStyle;

	// 日志区 Danger 行样式（UX-S12 高亮，预建避免每帧 new GUIStyle 的 GC 压力）
	private static GUIStyle _logDangerStyle;

	// 下方常显区状态行专用：与 _dimStyle 同外观，但开 wordWrap。
	// 不开在 _dimStyle 上是刻意的——_dimStyle 还被面包屑/自定义区块标题/队友信息行共用，
	// 那些地方走 CalcSize 判定（面包屑收起逻辑），一旦允许换行，CalcSize 会返回折行后的宽度，
	// 收起判定直接失效。
	private static GUIStyle _statusStyle;

	// 队友信息行/运行日志行专用：与 _dimStyle 同外观，但开 wordWrap。
	// 不开在 _dimStyle 上的理由同上（面包屑 CalcSize 判定），但队友页的 SteamID/位置/效果行、
	// 房间管理页的日志原文都是超长文本，无 wordWrap 会横向溢出窗口、且高度永远按单行算
	// （A2/H1 同源问题在队友页的遗漏）——这里单独开换行，宽度与高度都随文本自适应。
	private static GUIStyle _dimWrapStyle;

	// 鱼类下拉缓存
	private List<Creature> _fishPrefabs;

	private string[] _fishNames;

	private int _fishSel;

	// 任务物品下拉缓存
	private List<Item> _questItems;

	private string[] _questNames;

	private int _questSel;

	// ---- 第二批：SP-01 生成浏览器状态 ----
	private List<Item> _spawnList;

	private int _spawnCat;

	private int _spawnSel;

	private string _spawnSearch = "";

	private int _spawnCountWish = 1;

	// 变体加工开关（熟化 / 闪光）
	private bool _spawnCook;

	private bool _spawnDrip;

	// 过滤结果缓存（搜索串或分类变化时重建，避免每帧全表 GetComponent）
	private List<int> _spawnFiltered;

	private string _spawnFilterKey = "|-1";

	// 生成列表竖列的内嵌滚动位置
	private Vector2 _spawnScroll;

	/// <summary>ESP 最大绘制距离（米）——超出不画，避免远处一片糊字（L7）。</summary>
	private const float EspMaxDist = 100f;

	/// <summary>ESP 单帧最多绘制的标签数（再多就是满屏噪声）。</summary>
	private const int EspMaxLabels = 80;

	/// <summary>生成页签索引（专用布局判断用）。</summary>
	private int SpawnTabIndex => Array.FindIndex(Pages, p => p.Title == "生成");

	/// <summary>队友页签索引（PlayerMonitor 聚焦刷新判定用）。</summary>
	private int TeammateTabIndex => Array.FindIndex(Pages, p => p.Title == "队友");

	/// <summary>SP-01 分类页签（索引即 _spawnCat：0=全部不过滤）。</summary>
	private static readonly string[] SpawnCats = new string[8]
	{
		"全部", "Boss", "鸟", "鱼", "生物", "武器", "工具", "物品"
	};

	// ---- 第二批：MN-01 金钱输入 ----
	private string _moneyText = "";

	// IMGUI 焦点在自家文本框时置位（Update 里据此跳过 F9 切换，防打字误关面板）
	private bool _uiTyping;

	// ---- 第二批：FS-13 给饵选择 ----
	private List<string> _baitNames;

	private int _baitSel;

	// ---- 第二批：TP-01 玩家选择（传到/拉来共用一个游标）----
	private int _playerSel;

	// ---- 第七批：PlayerOps 队友操作 UI 状态 ----
	private int _teammateSel;
	private string _teammateMoneyText = "1000";
	private string _teammateLoopText = "1";
	private string _teammateIntervalText = "1";
	private Vector2 _teammateLogScroll;
	private int _teammateLogFilter;
	private readonly Dictionary<string, float> _confirmUntil = new Dictionary<string, float>();

	// UX-S6 操作反馈 toast：最近一次 OperationLog 新增条目，显示 1.5s 后消失
	private int _lastLogCount;
	private string _opToastText = "";
	private float _opToastUntil;

	// 面板自身的提示语（如"拖拽缩放已关闭窗口自适应"），优先级高于操作 toast。
	// 没有它时，拖一下右下角就永久静默关掉自适应（H5），用户只会以为"高度怎么不动了"。
	private string _hintText = "";
	private float _hintUntil;

	// ---- 第二批：VS-01 ESP 缓存（0.5 秒扫一次，避免每帧 FindObjectsOfType）----
	private List<Creature> _espCache;

	private float _espNextScan;

	// ---- 第二批：VS-02 自由相机运行态 ----
	private PlayerCamera _freecamComp;

	private float _fcYaw;

	private float _fcPitch;

	// ---- 第三批：VS-03 恢复态（FOV 覆盖前记住游戏原值，关闭时写回）----
	private bool _fovSaved;

	private float _savedFov = 90f;

	// ---- 第三批：CB-01 皮肤机奖品选择（仅列带皮肤预设的物品）----
	private List<Item> _skinItems;

	private int _skinSel;

	// ---- 第四批：AM-01 组件缓存（滑条写字段后须 Invoke CacheSettings 重算派生缓存）----
	private PlayerAimAssist _aimComp;

	// ---- 第四批：IT-01 售价输入框 ----
	private string _itemWorthText = "";

	// 键盘导航进行中：置位时抑制鼠标 hover 高亮，避免"选中行 + 悬停行"同时亮
	private bool _kbNav;

	// 生成页列表高度（自适应/手动两种算法，见 DrawSpawnBrowser）
	private float _spawnListH = 320f;

	// 生成页"列表以上"固定部分的高度（直生行 + 分隔标题 + 刷新 + 搜索 + 分类），
	// 在 ScrollView 之前正推测量（Repaint），上一帧值供本帧算列表高（不再 _contentH - 列表高反推）
	private float _spawnOtherH = 140f;

	// 生成页底部固定控件高度（数量行 + 生成按钮行），同样在画完后正推测量（Repaint）。
	// 与 _spawnOtherH 一起构成"列表以外"的完整高度预算，两处都是恒定值，喂给 avail 不再抖动
	private float _spawnBottomH = 64f;

	// 生成页视口顶（pageTop）缓存：DrawSpawnBrowser 是无参委托（CheatMenuModel 的
	// Custom(string, Action) 要求），pageTop 只能由 DrawSpawnPageFull 写入、这里读取
	private float _spawnPageTop;

	// 生成页布局输入快照（Layout 事件冻结）：_viewTop/_belowBudget/_windowRect 都在 Repaint
	// 被当场更新，若不冻结，DrawSpawnBrowser 在 Layout/Repaint 两阶段会算出不同的 _spawnListH，
	// BeginScrollView 两阶段高度参数不一致 → 底部控件跳变（抖动根因环 2）。
	private float _spawnInputTop;
	private float _spawnInputBelow;
	private float _spawnInputWinH;

	// ESP 绘制排序缓存（远→近，近处标签后画才压得住远处的）
	private readonly List<Creature> _espDraw = new List<Creature>();

	// ESP 排序用的相机位置与比较器（缓存比较器，免得每帧新建委托）
	private Vector3 _espCamPos;

	private Comparison<Creature> _espCmp;

	// _errors 队列锁（日志回调可能来自非主线程）
	private readonly object _errLock = new object();

	// ---- 第四批：KB-01 热键槽位（F1-F5，值=热键动作表索引，0=未绑定）----
	internal readonly int[] _hotkeySlots = new int[5];

	public static void EnsureExists()
	{
		if ((bool)_instance)
		{
			return;
		}
		GameObject gameObject = new GameObject("HTFCheatPanelHost");
		UnityEngine.Object.DontDestroyOnLoad(gameObject);
		_instance = gameObject.AddComponent<CheatPanel>();
	}

	private void Awake()
	{
		_instance = this;
	}

	private void OnEnable()
	{
		Application.logMessageReceived += OnLogMessage;
		CheatOps.Log("panel ready (F9)");
	}

	private void OnDisable()
	{
		Application.logMessageReceived -= OnLogMessage;
	}

	private void OnLogMessage(string condition, string stackTrace, LogType type)
	{
		if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
		{
			return;
		}
		string text = condition ?? "";
		if (text.Length > 120)
		{
			text = text.Substring(0, 120);
		}
		// 日志回调可能来自非主线程，队列必须加锁（OnGUI 侧同一把锁读）
		lock (_errLock)
		{
			_errors.Enqueue(type + ": " + text);
			while (_errors.Count > 20)
			{
				_errors.Dequeue();
			}
		}
	}

	private void Update()
	{
		if (!CheatState.Enabled)
		{
			return;
		}
		if (CheatState.FreeCamActive)
		{
			UpdateFreeCam();
		}
		PlayerOps.Update();
		// UX-S9：只在队友页有选中时把刷新提到 0.2s；其它页保持 0.5s 省开销
		PlayerMonitor.FocusedIndex = (_tab == TeammateTabIndex) ? _teammateSel : -1;
		PlayerMonitor.Refresh();
		// PlayerOps 循环任务驱动：批量操作都是"注册任务 + 等 Tick 执行"，
		// 少了这一行，点任何批量按钮都只注册不执行（功能全哑）
		TaskScheduler.Tick();
		if (ChatManager.IsTyping || _uiTyping)
		{
			return;
		}
		if (Input.GetKeyDown(KeyCode.F9))
		{
			_open = !_open;
			if (_open)
			{
				// 刚打开时置位：OnGUI 里 FocusWindow，免去先点一下才能键盘导航
				_needsFocus = true;
			}
		}
		if (CheatState.HotkeysOn)
		{
			HandleHotkeys();
		}
	}

	// ---- KB-01 热键：F1-F5 五槽位，默认全部未绑定；总开关关时整组不读键 ----

	/// <summary>热键动作表（索引 0 固定为"未绑定"占位）。</summary>
	internal static readonly string[] HotkeyActions = new string[12]
	{
		"未绑定", "无敌", "一击必杀", "飞行模式", "ESP 透视", "自由相机",
		"死亡不掉落", "鸟不偷窃", "永不失鱼", "自动收线", "赌局偏置", "静默弹道"
	};

	private void InvokeHotkeyAction(int action)
	{
		switch (action)
		{
		case 1:
			CheatState.ToggleSelfInvincible();
			break;
		case 2:
			CheatState.ToggleOneShotKill();
			break;
		case 3:
			CheatState.ToggleFlying();
			break;
		case 4:
			CheatState.ToggleEsp();
			break;
		case 5:
			if (CheatState._freeCam)
			{
				ExitFreeCam();
			}
			else
			{
				EnterFreeCam();
			}
			break;
		case 6:
			CheatState.ToggleKeepInventory();
			break;
		case 7:
			CheatState.ToggleBirdsDontSteal();
			break;
		case 8:
			CheatState.ToggleNeverLoseFish();
			break;
		case 9:
			CheatState.ToggleAutoReelIn();
			break;
		case 10:
			CheatState.ToggleCasinoBias();
			break;
		case 11:
			CheatState.ToggleSilentAim();
			break;
		default:
			return;
		}
		CheatOps.Log("op=hotkey action=" + HotkeyActions[action]);
	}

	private void HandleHotkeys()
	{
		for (int i = 0; i < _hotkeySlots.Length; i++)
		{
			if (_hotkeySlots[i] > 0 && Input.GetKeyDown(KeyCode.F1 + i))
			{
				InvokeHotkeyAction(_hotkeySlots[i]);
			}
		}
	}

	// 把内置 GUISkin 的所有默认背景贴图（box/scrollbar/thumb/button/toggle/textField 等）
	// 全部置空：只留纯底色，避免任何白/米褐圆角贴图从菜单里漏出来。
	private static void ClearSkinBackgrounds(GUISkin skin)
	{
		List<GUIStyle> styles = new List<GUIStyle>();
		FieldInfo[] fields = skin.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		foreach (FieldInfo field in fields)
		{
			if (typeof(GUIStyle).IsAssignableFrom(field.FieldType))
			{
				if (field.GetValue(skin) is GUIStyle s)
				{
					styles.Add(s);
				}
			}
			else if (typeof(GUIStyle[]).IsAssignableFrom(field.FieldType) && field.GetValue(skin) is GUIStyle[] arr)
			{
				styles.AddRange(arr);
			}
		}
		foreach (GUIStyle style in styles)
		{
			NullBg(style.normal);
			NullBg(style.hover);
			NullBg(style.active);
			NullBg(style.focused);
			NullBg(style.onNormal);
			NullBg(style.onHover);
			NullBg(style.onActive);
			NullBg(style.onFocused);
		}
	}

	private static void NullBg(GUIStyleState st)
	{
		if (st != null)
		{
			st.background = null;
		}
	}

	// 中文字体候选链：逐个尝试并用 HasCharacter('中') 校验字形，
	// 避免精简版系统 / 字体被禁用时整屏豆腐块（F6）
	private static readonly string[] CnFontNames = new string[8]
	{
		"Microsoft YaHei UI", "Microsoft YaHei", "DengXian", "SimHei",
		"Noto Sans CJK SC", "Source Han Sans SC", "SimSun", "Arial Unicode MS"
	};

	private static Font LoadCnFont()
	{
		for (int i = 0; i < CnFontNames.Length; i++)
		{
			try
			{
				Font f = Font.CreateDynamicFontFromOSFont(CnFontNames[i], 14);
				if ((bool)f && f.HasCharacter('中'))
				{
					return f;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	// 从系统字体构建带中文字形的皮肤，并预建主题样式。
	// 中文候选全失败时保留游戏原字体（英文仍可读，不至于整屏豆腐块）。
	private static void EnsureSkin()
	{
		if (_skin != null)
		{
			return;
		}
		Font cnFont = LoadCnFont();
		_skin = UnityEngine.Object.Instantiate(GUI.skin);
		// 先清掉所有内置背景贴图（白/米褐圆角贴图会从 window/box 漏出），
		// 再给需要视觉反馈的交互控件重建纯色背景——只清不建会让按钮/滑条/滚动条全部"隐身"（F1）
		ClearSkinBackgrounds(_skin);
		RebuildControlSkins(_skin);
		if (!(cnFont == null))
		{
			_skin.font = cnFont;
			_skin.box.font = cnFont;
			_skin.label.font = cnFont;
			_skin.button.font = cnFont;
			_skin.toggle.font = cnFont;
			_skin.window.font = cnFont;
			_skin.textField.font = cnFont;
			_skin.textArea.font = cnFont;
		}
		_skin.label.fontSize = 15;
		_skin.button.fontSize = 15;
		_skin.button.alignment = TextAnchor.MiddleCenter;   // M9-2：按钮文字默认居中（截图里"复制全部异常"左对齐就是因为 alignment 没显式设）
		_skin.toggle.fontSize = 15;
		_skin.textField.fontSize = 15;
		_skin.textArea.fontSize = 15;
		// 默认文字色必须显式兜底（F2）：游戏 skin 若沿用 Unity 默认近黑 label，
		// 所有不传 style 的 GUILayout.Label（错误框/诊断页/自定义区块）在深色窗口上完全不可见
		SetTextColor(_skin.label, C_Text);
		SetTextColor(_skin.box, C_Text);
		SetTextColor(_skin.button, C_Text);
		SetTextColor(_skin.toggle, C_Text);
		SetTextColor(_skin.textField, C_Text);
		SetTextColor(_skin.textArea, C_Text);

		_winStyle = new GUIStyle(_skin.window);
		// 窗口自身的不透明背景（F4-2/S10-2）：原来的 DrawRect 单层 + alpha=0.99 在某些
		// 渲染路径下会透出游戏场景，让文字在复杂背景上难以辨认。把 window 的 normal.background
		// 直接设成纯色贴图，GUILayout.Window 的 chrome 绘制就一定是完全不透明的
		_winStyle.normal.background = Solid(1, 1, C_BgWindow);
		_winStyle.border = new RectOffset(0, 0, 0, 0);
		_winStyle.padding = new RectOffset(0, 0, 0, 0);

		_rowStyle = new GUIStyle(_skin.label);
		_rowStyle.alignment = TextAnchor.MiddleLeft;
		_rowStyle.fontSize = 15;
		_rowStyle.normal.textColor = Color.white;

		_rightStyle = new GUIStyle(_skin.label);
		_rightStyle.alignment = TextAnchor.MiddleRight;
		_rightStyle.fontSize = 15;
		_rightStyle.normal.textColor = Color.white;

		_dimStyle = new GUIStyle(_skin.label);
		_dimStyle.alignment = TextAnchor.MiddleLeft;
		_dimStyle.fontSize = 13;
		_dimStyle.normal.textColor = Color.white;

		_titleStyle = new GUIStyle(_skin.label);
		_titleStyle.alignment = TextAnchor.MiddleLeft;
		_titleStyle.fontSize = 20;
		_titleStyle.fontStyle = FontStyle.Bold;
		_titleStyle.normal.textColor = Color.white;

		_centerStyle = new GUIStyle(_skin.label);
		_centerStyle.alignment = TextAnchor.MiddleCenter;
		_centerStyle.fontSize = 15;
		_centerStyle.normal.textColor = Color.white;

		// 错误框样式：wordWrap=true 让长错误（SSL/堆栈）自动换行而不溢出窗口，
		// 配合 DrawErrorBox 里的固定 2 行高度截断，完整文本仍可通过"复制全部"按钮获取
		_errorStyle = new GUIStyle(_skin.label);
		_errorStyle.fontSize = 13;
		_errorStyle.alignment = TextAnchor.MiddleLeft;
		_errorStyle.wordWrap = true;
		_errorStyle.normal.textColor = Color.white;

		// 日志区 Danger 行：暗红底 + 亮红字，比纯改 GUI.color 更醒目（UX-S12）
		_logDangerStyle = new GUIStyle(_skin.label);
		_logDangerStyle.fontSize = 13;
		_logDangerStyle.alignment = TextAnchor.MiddleLeft;
		_logDangerStyle.wordWrap = true;
		_logDangerStyle.clipping = TextClipping.Clip;
		_logDangerStyle.normal.background = Solid(1, 1, new Color(0.45f, 0.08f, 0.08f, 0.9f));
		_logDangerStyle.normal.textColor = new Color(1f, 0.65f, 0.65f);

		// 状态行：唯一与 _dimStyle 的差别是 wordWrap（理由见字段注释）
		_statusStyle = new GUIStyle(_skin.label);
		_statusStyle.alignment = TextAnchor.MiddleLeft;
		_statusStyle.fontSize = 13;
		_statusStyle.wordWrap = true;
		_statusStyle.clipping = TextClipping.Clip;
		_statusStyle.normal.textColor = Color.white;

		// 队友信息行/日志行：同 _dimStyle，但开 wordWrap，长文本换行不溢出（理由见字段注释）
		_dimWrapStyle = new GUIStyle(_dimStyle);
		_dimWrapStyle.wordWrap = true;
		_dimWrapStyle.clipping = TextClipping.Clip;
	}

	// ---- 交互控件皮肤重建（F1）----
	// ClearSkinBackgrounds 把 button/toggle/textField/slider/scrollbar 的背景一并清空后，
	// 这些控件在深色窗口上等于"隐身"：按钮没边框、滑条没轨道、列表右侧滚动条彻底消失。
	// 这里用纯色贴图把它们重新上色——纯色无圆角，不会像内置贴图那样漏出白/米褐边。
	// 注意：滑条/滚动条必须显式给 fixed 尺寸，1x1 贴图撑不出可见的轨道与滑块。

	private static void RebuildControlSkins(GUISkin skin)
	{
		Texture2D btn = Solid(1, 1, new Color(0.16f, 0.16f, 0.21f, 0.95f));
		Texture2D btnHover = Solid(1, 1, new Color(0.23f, 0.24f, 0.31f, 0.95f));
		Texture2D btnActive = Solid(1, 1, new Color(0.30f, 0.32f, 0.42f, 0.95f));
		Texture2D btnOn = Solid(1, 1, new Color(0.14f, 0.44f, 0.55f, 0.95f));
		Texture2D field = Solid(1, 1, new Color(0.07f, 0.07f, 0.11f, 0.98f));
		Texture2D fieldOn = Solid(1, 1, new Color(0.11f, 0.16f, 0.24f, 0.98f));
		Texture2D track = Solid(1, 1, new Color(0.17f, 0.17f, 0.23f, 0.95f));
		Texture2D thumb = Solid(10, 14, C_Accent);
		Texture2D bar = Solid(1, 1, new Color(0.10f, 0.10f, 0.14f, 0.95f));
		Texture2D barThumb = Solid(10, 10, new Color(0.36f, 0.39f, 0.46f, 0.95f));

		// 按钮族（含 Toolbar 用的 buttonleft/mid/right）：三态 + 选中态
		GUIStyle[] buttons = new GUIStyle[4]
		{
			skin.button, skin.FindStyle("buttonleft"), skin.FindStyle("buttonmid"), skin.FindStyle("buttonright")
		};
		for (int i = 0; i < buttons.Length; i++)
		{
			if (buttons[i] == null)
			{
				continue;
			}
			FlatBg(buttons[i], btn, btnHover, btnActive);
			FlatOn(buttons[i], btnOn, btnOn, btnOn, Color.white);
		}
		// 输入框：聚焦时底色提亮，否则看不出光标落在哪个框
		FlatBg(skin.textField, field, fieldOn, fieldOn);
		FlatBg(skin.textArea, field, fieldOn, fieldOn);
		// box：自定义区块里当"当前选中项"容器用，给暗底才和行背景区分得开
		FlatBg(skin.box, Solid(1, 1, new Color(0.12f, 0.12f, 0.16f, 0.92f)), null, null);

		// 横向滑条（主菜单行内 + 自定义区块的 GUILayout.HorizontalSlider 共用）
		FlatBg(skin.horizontalSlider, track, track, track);
		Size(skin.horizontalSlider, 0f, 14f, stretchW: true, stretchH: false);
		FlatBg(skin.horizontalSliderThumb, thumb, thumb, thumb);
		Size(skin.horizontalSliderThumb, 10f, 14f, stretchW: false, stretchH: false);
		FlatBg(skin.verticalSlider, track, track, track);
		Size(skin.verticalSlider, 14f, 0f, stretchW: false, stretchH: true);
		FlatBg(skin.verticalSliderThumb, thumb, thumb, thumb);
		Size(skin.verticalSliderThumb, 14f, 10f, stretchW: false, stretchH: false);

		// 滚动条：背景被清空后列表右侧滚动条彻底消失，用户不知道列表能滚、滚到哪
		FlatBg(skin.verticalScrollbar, bar, bar, bar);
		Size(skin.verticalScrollbar, 10f, 0f, stretchW: false, stretchH: true);
		FlatBg(skin.verticalScrollbarThumb, barThumb, barThumb, barThumb);
		Size(skin.verticalScrollbarThumb, 10f, 0f, stretchW: false, stretchH: false);
		FlatBg(skin.horizontalScrollbar, bar, bar, bar);
		Size(skin.horizontalScrollbar, 0f, 10f, stretchW: true, stretchH: false);
		FlatBg(skin.horizontalScrollbarThumb, barThumb, barThumb, barThumb);
		Size(skin.horizontalScrollbarThumb, 0f, 10f, stretchW: false, stretchH: false);

		// toggle 不重建背景：内置 toggle 的复选框就是背景图的一部分，
		// 换成整块纯色会把整行（含文字）涂成实心条。复选框改由自绘 DrawToggle 提供。
	}

	private static Texture2D Solid(int w, int h, Color c)
	{
		Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
		Color[] px = new Color[w * h];
		for (int i = 0; i < px.Length; i++)
		{
			px[i] = c;
		}
		t.SetPixels(px);
		t.Apply(false, false);
		t.hideFlags = HideFlags.HideAndDontSave;
		return t;
	}

	private static void FlatBg(GUIStyle s, Texture2D normal, Texture2D hover, Texture2D active)
	{
		if (s == null)
		{
			return;
		}
		s.border = new RectOffset(0, 0, 0, 0);
		if (normal != null)
		{
			s.normal.background = normal;
			s.focused.background = normal;
		}
		if (hover != null)
		{
			s.hover.background = hover;
		}
		if (active != null)
		{
			s.active.background = active;
		}
	}

	private static void FlatOn(GUIStyle s, Texture2D on, Texture2D onHover, Texture2D onActive, Color text)
	{
		if (s == null)
		{
			return;
		}
		s.onNormal.background = on;
		s.onNormal.textColor = text;
		s.onHover.background = onHover;
		s.onHover.textColor = text;
		s.onActive.background = onActive;
		s.onActive.textColor = text;
		s.onFocused.background = on;
		s.onFocused.textColor = text;
	}

	private static void Size(GUIStyle s, float w, float h, bool stretchW, bool stretchH)
	{
		if (s == null)
		{
			return;
		}
		if (w > 0f)
		{
			s.fixedWidth = w;
		}
		if (h > 0f)
		{
			s.fixedHeight = h;
		}
		s.stretchWidth = stretchW;
		s.stretchHeight = stretchH;
	}

	private static void SetTextColor(GUIStyle s, Color c)
	{
		if (s == null)
		{
			return;
		}
		s.normal.textColor = c;
		s.hover.textColor = c;
		s.active.textColor = c;
		s.focused.textColor = c;
		s.onNormal.textColor = c;
		s.onHover.textColor = c;
		s.onActive.textColor = c;
		s.onFocused.textColor = c;
	}

	/// <summary>按 CheatState.FontScale 实时覆写字号（基准字号保持 15/13/20，行高走 RowH 联动）。</summary>
	private void ApplyFontScale()
	{
		float scale = CheatState.FontScale;
		if (!Mathf.Approximately(scale, _appliedFontScale))
		{
			_appliedFontScale = scale;
			// 档位区宽度缓存是按旧字号测的，字号一变必须作废，否则 Cycle 行右侧区会一直按旧宽度留白
			_cycleRightW.Clear();
		}
		int fs = Mathf.RoundToInt(15f * scale);
		int fsDim = Mathf.RoundToInt(13f * scale);
		_rowStyle.fontSize = fs;
		_rightStyle.fontSize = fs;
		_centerStyle.fontSize = fs;
		_dimStyle.fontSize = fsDim;
		_statusStyle.fontSize = fsDim;
		_dimWrapStyle.fontSize = fsDim;
		_errorStyle.fontSize = fsDim;
		_logDangerStyle.fontSize = fsDim;
		_titleStyle.fontSize = Mathf.RoundToInt(20f * scale);
		_skin.label.fontSize = fs;
		_skin.button.fontSize = fs;
		_skin.toggle.fontSize = fs;
		_skin.textField.fontSize = fs;
		_skin.box.fontSize = fs;
		_skin.textArea.fontSize = fs;
		// 分类 Toolbar（GUILayout.Toolbar 用的是 buttonleft/mid/right 三个子样式，不是 button）：
		// 旧写法在生成页把它钉死在 12px，导致"用户把字号调到 ×1.6，唯独分类行纹丝不动"（A11）。
		// 现在跟随字号，但压在上限 16 以内且下限 12 —— 8 个分类平分宽度，再大就会互相挤掉文字。
		int tbFs = Mathf.Clamp(fs, 12, 16);
		SetToolbarFontSize(tbFs);
	}

	private void SetToolbarFontSize(int fs)
	{
		GUIStyle m = _skin.FindStyle("buttonmid");
		if (m != null)
		{
			m.fontSize = fs;
			m.padding = new RectOffset(2, 2, 2, 2);
		}
		GUIStyle l = _skin.FindStyle("buttonleft");
		if (l != null)
		{
			l.fontSize = fs;
			l.padding = new RectOffset(2, 2, 2, 2);
		}
		GUIStyle r = _skin.FindStyle("buttonright");
		if (r != null)
		{
			r.fontSize = fs;
			r.padding = new RectOffset(2, 2, 2, 2);
		}
	}

	private void OnGUI()
	{
		if (!CheatState.Enabled)
		{
			return;
		}
		EnsureSkin();
		// 记下游戏原 skin，离开时还回去——污染全局 GUI.skin 会影响同帧后续的其他 OnGUI（L10）
		GUISkin prevSkin = GUI.skin;
		GUI.skin = _skin;
		// VS-01 ESP 是独立叠加层：面板关着也要画（纯本地渲染零 RPC）
		DrawEsp();
		if (_open)
		{
			// 面板期间强制解锁鼠标（游戏每帧会上锁，OnGUI 在其之后执行所以必赢）
			Cursor.lockState = CursorLockMode.None;
			Cursor.visible = true;
		}
		else if (CheatState.FreeCamActive)
		{
			// 自由相机（面板关着时）：锁鼠隐藏，原始鼠标增量喂镜头转向
			Cursor.lockState = CursorLockMode.Locked;
			Cursor.visible = false;
		}
		_uiTyping = false;
		if (!_open)
		{
			if (CheatPlugin.PatchFailed)
			{
				Color c = GUI.color;
				GUI.color = Color.red;
				GUI.Label(new Rect(20f, 20f, 420f, 22f), "[F9] 作弊器初始化失败——请重启游戏后重新注入！");
				GUI.color = c;
			}
			else
			{
				GUI.Label(new Rect(20f, 20f, 220f, 22f), "[F9] 作弊菜单");
			}
			GUI.skin = prevSkin;
			return;
		}
		// 点面板外任意区域直接收工：关面板 + 输入自动交还（忘记按 F9 也能一键回游戏）
		if (Event.current.type == EventType.MouseDown && !_windowRect.Contains(Event.current.mousePosition))
		{
			_open = false;
			CheatOps.Log("panel closed by outside click");
			Event.current.Use();
			GUI.skin = prevSkin;
			return;
		}
		// 上限跟随屏幕实际尺寸，避免小屏上把窗口拖出屏幕外（M11）
		float maxH = Mathf.Max(280f, Screen.height - 20f);
		float maxW = Mathf.Max(340f, Screen.width - 20f);
		// 宽度自适应（W1）：字号放大时内容整体放大，若宽度仍锁死 380，
		// 三段式布局（标签 / 滑条 / 右侧档位）里的固定像素宽度会被逐个挤爆 —— 这是
		// "字号调大就显示不全"的真正放大器。宽度按基准宽等比跟随，用户拖过右下角后交还控制权。
		ApplyWindowWidth(maxW);
		// manualH = 我们维护的窗口目标高度（上一帧落地/拖拽后的值）。
		// GUILayout.Window 是自动布局窗口，返回值会按内容把窗口撑高；把它单独存下来，
		// 手动模式用它"钉住"高度、拒绝返回值漂移（否则"拖拽后无限变高"，见下方落地注释）。
		float manualH = _windowRect.height;
		_windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, GUIContent.none, _winStyle);
		// 刚打开时把键盘焦点给窗口：IMGUI 窗口未获焦前不接收按键，必须先点一下才能导航
		if (_needsFocus)
		{
			_needsFocus = false;
			GUI.FocusWindow(WindowId);
		}
		// 缩放手柄的尺寸增量在窗口返回值之后落地（回调内改会被每帧覆盖——缩放失效根因）
		if (_resizeDelta != Vector2.zero)
		{
			// 拖拽增量只累加到 manualH 上，而不是叠加到 GUILayout.Window 的返回值上：
			// 若把增量加到"已被自动布局撑高"的返回高度上，等于每帧把撑高量再累加一遍，
			// 拖拽期间窗口高度会被指数级放大。
			manualH = Mathf.Clamp(manualH + _resizeDelta.y, 280f, maxH);
			_windowRect.height = manualH;
			_windowRect.width = Mathf.Clamp(_windowRect.width + _resizeDelta.x, 340f, maxW);
			// 横向拖过 = 用户自己定宽度，此后不再自动跟随字号改宽
			if (Mathf.Abs(_resizeDelta.x) > 0.5f)
			{
				_manualWidth = true;
			}
			_resizeDelta = Vector2.zero;
		}
		// 高度落地：AutoFit 模式按内容自适应；手动/拖拽模式一律钉住 manualH。
		// 关键改动：这里用 _scrollH 而不是 _contentH。_scrollH 在 DrawWindow 里已经按
		// "屏幕剩余空间"夹过上界，所以窗口高永远 ≤ maxH，下方常显区（状态行/错误框/状态栏）
		// 不可能被挤到窗口外面去 —— 旧写法拿未夹的 _contentH 计算，内容一多就把底部顶出去（H1）。
		// 三项输入都只在 Repaint 阶段被更新（见 DrawWindow），这里读到的始终是稳定缓存值，
		// 不会随 Layout/MouseDrag 事件漂移 —— 拖动滑条时高度不再抖。
		// 手动模式（拖过手柄后 AutoFit=false）为什么必须钉住 manualH：
		// 手动模式下滚动区高 = 窗口剩余（_windowRect.height − chrome），若放任窗口高度被
		// GUILayout.Window 的自动布局返回值接管，返回值每比上一帧多 1px，下一帧滚动区就跟着
		// 多 1px、内容所需高度再多 1px、返回值再多 1px —— 每帧 +1px 的正反馈，表现就是
		// "拖拽后高度无限变高"。AutoFit 模式因为有本段落地每帧压回 target，这个反馈被压制，
		// 所以拖拽前一切正常。旧写法落地条件写死 AutoFitHeight && !_resizing，
		// 手动模式下整个落地被跳过 → 反馈无人压制 → 无限变高。
		if (Event.current.type == EventType.Repaint && _viewTop > 0f)
		{
			if (CheatState.AutoFitHeight && !_resizing)
			{
				float target = Mathf.Clamp(_viewTop + _scrollH + _belowBudget + WINDOW_PAD, 280f, maxH);
				_windowRect.height = target;
			}
			else
			{
				_windowRect.height = Mathf.Clamp(manualH, 280f, maxH);
			}
		}
		_windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width - 10f));
		GUI.skin = prevSkin;
	}

	/// <summary>宽度随字号等比自适应；用户手动拖过宽度后永久让位（除非重新打开自适应开关）。</summary>
	private void ApplyWindowWidth(float maxW)
	{
		bool autoFit = CheatState.AutoFitHeight;
		if (autoFit && !_prevAutoFit)
		{
			// 重新打开自适应 = 把宽度控制权交还给面板
			_manualWidth = false;
		}
		_prevAutoFit = autoFit;
		if (!autoFit || _manualWidth || _resizing)
		{
			return;
		}
		_windowRect.width = Mathf.Clamp(BASE_W * CheatState.FontScale, 340f, maxW);
	}

	private void DrawWindow(int id)
	{
		Event ev0 = Event.current;
		if (ev0.type == EventType.MouseMove || ev0.type == EventType.MouseDown || ev0.type == EventType.MouseDrag)
		{
			// 鼠标一动就交还高亮权：否则键盘选中行与鼠标悬停行会同时亮（S5）
			_kbNav = false;
		}
		// 输入优先：缩放手柄的命中判定必须在最前——ScrollView 滚动条与 GUI.DragWindow
		// 都按绘制顺序抢鼠标事件，放末尾的旧写法永远收不到 MouseDown（缩放失效根因）
		HandleResizeInput();
		// 键盘导航在绘制前处理：先改 _selIndex，绘制时才能记录新选中行 rect 供自动滚动
		HandleKeyboardNav();
		// 字号随滑条实时生效（行高/文本/滑条全部联动）
		ApplyFontScale();
		// 错误快照在 Layout 阶段冻结（每帧第一个 GUI 事件）：Layout 与 Repaint 两阶段的
		// DrawErrorBox 都读这份快照，控件数量恒等——错误入队瞬间不再抛 "group" 异常
		UpdateErrorSnapshot();
		DrawWindowBackground();
		DrawNeonTitle();
		if (CheatPlugin.PatchFailed)
		{
			DrawPatchFailed();
			DrawResizeHandleVisual();
			GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 36f));
			return;
		}
		// 标题是绝对坐标绘制，不占 GUILayout 流高度；显式 Space 把页签/面包屑压到标题下方，
		// 且高度跟随标题字号——×1.6 时 32px 的字塞进写死的 30px 里会上下裁掉（A5）
		GUILayout.Space(TitleH + 8f);
		DrawTabs();
		DrawBreadcrumb();
		// ===== 生成页抖动根因修复（环 2 冻结）=====
		// Layout 是每帧第一个事件，此刻 _viewTop/_belowBudget/_windowRect 都是上一帧 Repaint
		// 的产物。把它们冻结成生成页专用快照：当帧 Layout/Repaint/MouseDrag 读同一份值，
		// _spawnListH 两阶段严格一致，BeginScrollView 高度不再"Layout 旧值 / Repaint 新值"跳变。
		if (Event.current.type == EventType.Layout)
		{
			_spawnInputTop = _viewTop;
			_spawnInputBelow = _belowBudget;
			_spawnInputWinH = _windowRect.height;
		}
		// ===== 闪烁根因修复（最关键的一处）=====
		// IMGUI 一帧会多次调用 OnGUI：MouseDrag / Layout / Repaint 各一次。
		// GUILayoutUtility.GetLastRect() 在 Layout 阶段返回"布局计算中的中间值"，
		// 在 Repaint 阶段才返回最终值。原代码不区分事件、无条件测量并写字段，
		// 于是 _viewTop/_contentH 一帧内被写多次、在 Layout 值与 Repaint 值之间跳变，
		// 自适应高度每帧抖动 → 拖动滑条（多了 MouseDrag 事件）时整个菜单疯狂闪烁。
		// 正解：所有测量 + 状态写入只在 Repaint 做，Layout/MouseDrag 沿用缓存的稳定值。
		bool isRepaint = Event.current.type == EventType.Repaint;
		if (isRepaint)
		{
			// 视口顶实测（窗口内容坐标系）：内容高、下方常显区、窗口高全部以它为基准。
			_viewTop = GUILayoutUtility.GetLastRect().yMax;
			UpdateBelowBudget();
			// 滚动区高度改为"正推"：旧写法用（窗口高 − 视口顶 − 下方预算）反推，
			// 于是窗口高与滚动区高互相喂值；一旦窗口高被屏幕高度夹住，多出来的内容不会变成滚动，
			// 而是把下方常显区整个顶到窗口外面（H1）。现在滚动区取"内容高"与"屏幕剩余空间"的较小者，
			// 窗口高 = 视口顶 + 滚动区 + 下方预算 ≤ 屏幕高，底部永远可见。
			// 手动尺寸模式（用户拖过缩放）则老老实实填满窗口剩余空间，不再有 90px 下限：
			// 窗口被拖到 280 高时，旧写法把滚动区强制撑到 90，反而把底部挤出窗口（H4）。
			float chrome = _viewTop + _belowBudget + WINDOW_PAD;
			float maxScroll = Mathf.Max(MIN_SCROLL, MaxWindowH - chrome);
			_scrollH = CheatState.AutoFitHeight
				? Mathf.Clamp(_contentH, MIN_SCROLL, maxScroll)
				: Mathf.Max(0f, _windowRect.height - chrome);
		}
		// 本帧绘制沿用缓存的稳定值（Layout 阶段绝不能拿当帧正在测的量去算布局）
		float belowBudget = _belowBudget;
		float scrollH = _scrollH;
		if (_tab == SpawnTabIndex && _pageStack.Count == 0)
		{
			// 生成页专用布局：列表用页面自己的滚动区，不嵌套外层滚动（嵌套会闪烁/双滚动/底部被裁）
			DrawSpawnPageFull();
		}
		else
		{
			// 滚动区起始 y 取自面包屑行——不能紧跟 BeginScrollView 调 GetLast（会抛 "group" 异常刷屏）
			// _scrollViewRect 只在 Repaint 更新：它参与 AutoScroll 判定，
			// 若在 Layout 阶段被写入中间值，选中行的自动滚动会每帧抖
			if (isRepaint)
			{
				_scrollViewRect = new Rect(0f, _viewTop, _windowRect.width, scrollH);
			}
			_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(scrollH));
			_contentTop = -1f;
			DrawCurrentPage();
			// 内容高 = 末行 yMax − 首行 y（F4）：取差值既不受滚动偏移影响，
			// 也不依赖 IMGUI 的坐标系约定；旧写法直接拿 yMax 当内容高，长列表永远撑不开窗口
			// 只在 Repaint 测量（同 _viewTop，避免 Layout 中间值污染）
			if (isRepaint && _contentTop >= 0f)
			{
				try
				{
					_contentH = Mathf.Max(0f, GUILayoutUtility.GetLastRect().yMax - _contentTop);
				}
				catch
				{
					// 空白页未绘制控件时 GetLast 抛异常，沿用上一帧高度
				}
			}
			GUILayout.EndScrollView();
		}
		GUILayout.Space(2f);
		Color c = GUI.color;
		GUI.color = C_TextDim;
		// 状态行高度走 UpdateBelowBudget 的实测值（1~3 行），文本也用同一时刻缓存的
		// _statusText —— 画出来的高度与算窗口高用的高度是同一份数据，所以既不裁也不闪。
		// 旧写法强制 Height(RowH) 单行 + wordWrap=false：Boss 状态 30+ 字符、操作 toast 是
		// 完整日志原文，默认字号就已经静默裁掉后半截（A2）。
		for (int i = 0; i < _statusH.Length; i++)
		{
			GUILayout.Label(_statusText[i], _statusStyle, GUILayout.Height(_statusH[i]));
		}
		GUI.color = c;
		DrawErrorBox();
		DrawStatusBar();
		DrawResizeHandleVisual();
		// AutoScroll 会写 _scroll.y（滚动偏移），而 _scroll 反过来参与 BeginScrollView 的布局。
		// 若在 Layout 阶段修改它，等于边算布局边改布局输入 → 内容上下抖。
		// 只在 Repaint 调用；生成页专用布局不走外层滚动，跳过（避免用陈旧 _scrollViewRect 干扰）
		if (isRepaint && (_tab != SpawnTabIndex || _pageStack.Count > 0))
		{
			AutoScroll();
		}
		FlushPendingLog();
		// 焦点在自家文本框时置位 → Update 跳过 F9 切换（防打金额/搜索词时误关面板）。
		// 与 HandleKeyboardNav 共用同一份判定，避免两处名单不一致
		_uiTyping = IsOurInputFocused();
		GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 36f));
	}

	// ---- 下方常显区：实测预算（H2 的根治）----

	/// <summary>窗口高度硬上限：整块内容（含下方常显区）不允许撑破屏幕。</summary>
	private static float MaxWindowH => Mathf.Max(280f, Screen.height - 20f);

	/// <summary>标题区高度：跟随标题字号，×1.6 时 32px 的字塞进写死的 30px 容器会上下裁掉（A5）。</summary>
	private float TitleH => Mathf.Max(30f, _titleStyle.fontSize * 1.45f);

	/// <summary>状态行可用文本宽度（与绘制时的实际可宽保持一致，宁可少 8px 也不能多测）。</summary>
	private float StatusWidth => Mathf.Max(80f, _windowRect.width - 16f);

	/// <summary>自定义区块（在滚动区内部）的可用宽度：减去右侧滚动条与两侧留白后粗估。</summary>
	private float ContentWidth => Mathf.Max(200f, _windowRect.width - 22f);

	/// <summary>状态行高度：按当帧文本实测需要几行（1~3 行），不再一律写死单行 RowH。</summary>
	private float StatusLineH(string text, float w)
	{
		if (string.IsNullOrEmpty(text))
		{
			// 空串也占一行：否则"活跃效果"从无到有时窗口会突然长高一截，反而更晃眼
			return RowH;
		}
		return Mathf.Clamp(_statusStyle.CalcHeight(new GUIContent(text), w), RowH, RowH * 3f);
	}

	/// <summary>错误行高度：真实 CalcHeight，最多 4 行。旧写法一律 2 行——长错误下半截直接消失（A12）。</summary>
	private float ErrorLineH(string text, float w)
	{
		if (string.IsNullOrEmpty(text))
		{
			return 0f;
		}
		return Mathf.Clamp(_errorStyle.CalcHeight(new GUIContent(text), w), RowH, RowH * 4f);
	}

	/// <summary>按当帧文本算出下方常显区每一块的高度并汇总成 _belowBudget。
	/// 只在 Repaint 调用；结果供"下一帧绘制"与"本帧窗口高计算"共用，
	/// 两份用途取自同一次测量，因此天然相等 —— 这是不闪又不裁的关键。</summary>
	/// <summary>错误快照：只在 Layout 阶段调用（每帧第一个 GUI 事件）。
	/// 把错误数量/文本/高度一次性冻结，Layout 与 Repaint 的绘制都读这份快照 ——
	/// 否则 Repaint 里更新 _errN 会让同帧两阶段的控件数差 1，
	/// 错误入队瞬间抛 "Getting control X's position in a group with only X controls"。</summary>
	private void UpdateErrorSnapshot()
	{
		if (Event.current.type != EventType.Layout)
		{
			return;
		}
		float w = StatusWidth;
		_errN = 0;
		_errCount = 0;
		_errBoxH = 0f;
		lock (_errLock)
		{
			if (_errors.Count > 0)
			{
				string[] all = _errors.ToArray();
				_errCount = all.Length;
				_errN = Mathf.Min(_errText.Length, all.Length);
				for (int i = 0; i < _errN; i++)
				{
					_errText[i] = all[all.Length - 1 - i];
				}
			}
		}
		if (_errN > 0)
		{
			for (int i = 0; i < _errN; i++)
			{
				_errH[i] = ErrorLineH(_errText[i], w);
				_errBoxH += _errH[i];
			}
			// "复制全部异常"按钮：高度按按钮样式实测（含上下 margin 的余量），
			// 拍脑袋给个常数要么留一大块空白、要么把按钮压出窗口
			string copyLabel = "复制全部异常 (" + _errCount + ")";
			_errBoxH += Mathf.Max(24f, _skin.button.CalcHeight(new GUIContent(copyLabel), w)) + 8f;
		}
	}

	private void UpdateBelowBudget()
	{
		float w = StatusWidth;
		// 顺序必须与下方绘制顺序一致：Boss / 钓鱼 / 活跃效果 / 操作 toast
		_statusText[0] = BuildBossStatus();
		_statusText[1] = BuildFishingStatus();
		_statusText[2] = BuildActiveEffectsStatus();
		_statusText[3] = BuildOpToast();
		float h = 8f;
		for (int i = 0; i < _statusText.Length; i++)
		{
			_statusH[i] = StatusLineH(_statusText[i], w);
			h += _statusH[i];
		}
		// 错误框预算直接取 Layout 阶段冻结的快照总高（含按钮）——
		// 这里不再碰队列/不再测高度，避免把"数量"更新时机拉回 Repaint 造成两阶段不一致
		h += _errBoxH;
		_statusBarH = Mathf.Max(24f, RowH);
		h += _statusBarH;

		// 长→短立刻生效（宁可窗口多留一截，也绝不能把状态栏挤到窗口外）；
		// 短→长延迟 0.8s（Boss 离场 tick 每帧在变，文字宽度常在换行临界点上下跳，
		// 没有这个阻尼窗口会每隔几帧抖一行 —— 这就是"预算必须恒定"那条铁律的替代品）
		float now = Time.unscaledTime;
		if (h >= _belowBudget)
		{
			_belowBudget = h;
			_belowShrinkAt = -1f;
		}
		else if (_belowShrinkAt < 0f)
		{
			_belowShrinkAt = now;
		}
		else if (now - _belowShrinkAt > 0.8f)
		{
			_belowBudget = h;
			_belowShrinkAt = -1f;
		}
	}

	// ---- 主题绘制辅助 ----

	/// <summary>错误队列元素数（日志回调可能来自非主线程，读写都走同一把锁）。</summary>
	private int ErrorCount
	{
		get
		{
			lock (_errLock)
			{
				return _errors.Count;
			}
		}
	}

	private static void DrawRect(Rect r, Color c)
	{
		Color old = GUI.color;
		GUI.color = c;
		GUI.DrawTexture(r, Texture2D.whiteTexture);
		GUI.color = old;
	}

	private static void DrawLabel(Rect r, string text, GUIStyle style, Color color)
	{
		Color old = GUI.color;
		GUI.color = color;
		GUI.Label(r, text, style);
		GUI.color = old;
	}

	// ---- 文本测量小工具（A3/A4/A7/A9：把写死的像素宽换成"按当前字号实测"）----
	// 背景：面板里遍布 GUILayout.Width(40f)、Width(18f) 这类常量，它们是按字号 ×1 目测出来的。
	// 字号一放大，中文每字约 1em，3 字 24px×3=72px 塞进 40px 的框里必然被静默裁掉。
	// 统一改走 CalcSize：放得下就维持原设计宽度（不浪费空间、不破坏对齐），放不下才按实测加宽。

	/// <summary>按当前字号测文本所需宽度，取"设计基准宽"与"实测宽+padding"的较大者。</summary>
	private static float TextW(GUIStyle style, string text, float baseW, float pad = 6f)
	{
		float need = style.CalcSize(new GUIContent(text)).x + pad;
		return Mathf.Max(baseW, need);
	}

	/// <summary>按可用宽度截断并补省略号。截断是单调的（前缀越长越宽），可以二分。</summary>
	private static string Ellipsize(GUIStyle style, string text, float maxW)
	{
		if (string.IsNullOrEmpty(text) || maxW <= 0f)
		{
			return "";
		}
		if (style.CalcSize(new GUIContent(text)).x <= maxW)
		{
			return text;
		}
		int lo = 1;
		int hi = text.Length;
		while (lo < hi)
		{
			int mid = (lo + hi + 1) / 2;
			string s = text.Substring(0, mid) + "…";
			if (style.CalcSize(new GUIContent(s)).x <= maxW)
			{
				lo = mid;
			}
			else
			{
				hi = mid - 1;
			}
		}
		return (lo >= text.Length) ? text : (text.Substring(0, lo) + "…");
	}

	/// <summary>GUILayout 宽度选项：不再写死像素，改按当前字号实测（不足则维持基准宽）。</summary>
	private static GUILayoutOption W(GUIStyle style, string text, float baseW, float pad = 6f)
	{
		return GUILayout.Width(TextW(style, text, baseW, pad));
	}

	/// <summary>Cycle 行右侧档位区宽度：按**所有档位**里最宽的那个测，
	/// 否则切档时右侧区宽度会跟着当前档位忽宽忽窄。</summary>
	private float CycleRightW(string[] options)
	{
		float w;
		if (_cycleRightW.TryGetValue(options, out w))
		{
			return w;
		}
		w = 0f;
		for (int i = 0; i < options.Length; i++)
		{
			w = Mathf.Max(w, _rightStyle.CalcSize(new GUIContent(options[i])).x);
		}
		w += 8f;
		_cycleRightW[options] = w;
		return w;
	}

	private void DrawWindowBackground()
	{
		DrawRect(new Rect(0f, 0f, _windowRect.width, _windowRect.height), C_BgWindow);
		DrawRect(new Rect(0f, 0f, _windowRect.width, 1f), C_Border);
		DrawRect(new Rect(0f, _windowRect.height - 1f, _windowRect.width, 1f), C_Border);
		DrawRect(new Rect(0f, 0f, 1f, _windowRect.height), C_Border);
		DrawRect(new Rect(_windowRect.width - 1f, 0f, 1f, _windowRect.height), C_Border);
	}

	private void DrawNeonTitle()
	{
		Rect titleRect = new Rect(10f, 5f, _windowRect.width - 20f, TitleH);
		// 发光层：2 层 × 4 方向偏移叠出光晕（S1：原 4 层 = 16 次 Label，
		// 20px 字号下第 3、4 层已经糊成重影还白砸一半 draw call，砍到 8 次视觉几乎无差）
		for (int i = 2; i >= 1; i--)
		{
			Color glow = C_Accent;
			glow.a = 0.07f * i;
			DrawLabel(new Rect(titleRect.x - i, titleRect.y, titleRect.width, titleRect.height), "HTF CHEAT", _titleStyle, glow);
			DrawLabel(new Rect(titleRect.x + i, titleRect.y, titleRect.width, titleRect.height), "HTF CHEAT", _titleStyle, glow);
			DrawLabel(new Rect(titleRect.x, titleRect.y - i, titleRect.width, titleRect.height), "HTF CHEAT", _titleStyle, glow);
			DrawLabel(new Rect(titleRect.x, titleRect.y + i, titleRect.width, titleRect.height), "HTF CHEAT", _titleStyle, glow);
		}
		DrawLabel(titleRect, "HTF CHEAT", _titleStyle, new Color(0.92f, 0.97f, 1f));
		// 版本号框写死 66×20 时，×1.6 的字号（24px）直接顶破 20px 高（A6）——宽高都按实测走
		float vW = Mathf.Max(66f, _rightStyle.CalcSize(new GUIContent(Version)).x + 12f);
		float vH = Mathf.Max(20f, _rightStyle.fontSize * 1.5f);
		DrawLabel(new Rect(_windowRect.width - vW - 10f, 5f + (TitleH - vH) * 0.5f, vW, vH), Version, _rightStyle, C_TextDim);
	}

	private void DrawTabs()
	{
		// 必须走 Pages 属性触发懒初始化——直接用 _pages 字段会在首次打开时 NRE
		CheatMenuPage[] pages = Pages;
		Rect bar = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
		float tabW = bar.width / pages.Length;
		for (int i = 0; i < pages.Length; i++)
		{
			Rect r = new Rect(bar.x + i * tabW, bar.y, tabW, 30f);
			bool sel = i == _tab;
			bool dim = pages[i].Title == "房主" && !CheatOps.IsServerReady;
			DrawRect(r, sel ? C_BgSel : new Color(1f, 1f, 1f, 0.03f));
			if (sel)
			{
				DrawRect(new Rect(r.x, r.y + r.height - 2f, r.width, 2f), C_Accent);
			}
			DrawLabel(r, pages[i].Title, _centerStyle, sel ? C_Accent : (dim ? C_TextDim : C_Text));
			Event ev = Event.current;
			if (ev.type == EventType.MouseDown && r.Contains(ev.mousePosition))
			{
				_tab = i;
				_pageStack.Clear();
				_crumbStack.Clear();
				SetCurrentList(pages[i].Entries, pushCurrent: true);
				ev.Use();
			}
		}
	}

	private void DrawBreadcrumb()
	{
		// S1-2：顶层页面（_crumbStack 为空）不画面包屑——页签已经显示了页签名，
		// 再画一遍完全是冗余，省下 ~26px 给内容区
		if (_crumbStack.Count < 1)
		{
			GUILayoutUtility.GetRect(0f, 2f, GUILayout.ExpandWidth(true));
			return;
		}
		Rect r = GUILayoutUtility.GetRect(0f, RowH, GUILayout.ExpandWidth(true));
		string[] parts = new string[_crumbStack.Count + 1];
		parts[0] = Pages[_tab].Title;
		for (int i = 0; i < _crumbStack.Count; i++)
		{
			parts[i + 1] = _crumbStack[i];
		}
		// 放不下就收起中间层级（S6）：旧写法直接裁切，深层子菜单里根本看不到当前在哪一级
		float avail = r.width - 16f;
		int keepTail = parts.Length - 1;
		string path = JoinCrumb(parts, keepTail);
		while (keepTail > 1 && _dimStyle.CalcSize(new GUIContent(path)).x > avail)
		{
			keepTail--;
			path = JoinCrumb(parts, keepTail);
		}
		DrawLabel(new Rect(r.x + 8f, r.y, avail, RowH), path, _dimStyle, C_Accent);
	}

	/// <summary>拼接面包屑：keepTail=末尾保留的层级数（不含根），不够放时用 … 收起中间层。</summary>
	private static string JoinCrumb(string[] parts, int keepTail)
	{
		if (keepTail >= parts.Length - 1)
		{
			return string.Join(" ▸ ", parts);
		}
		string s = parts[0] + " ▸ …";
		for (int i = parts.Length - keepTail; i < parts.Length; i++)
		{
			s += " ▸ " + parts[i];
		}
		return s;
	}

	private void DrawPatchFailed()
	{
		Color old = GUI.color;
		GUI.color = Color.red;
		GUILayout.Label("补丁挂载失败！典型原因：payload 与游戏版本不匹配（指纹校验被 --force 跳过）。");
		GUILayout.Label("解决：退出游戏，确认游戏为 R13fix 后重新注入。");
		if (!string.IsNullOrEmpty(CheatPlugin.InitError))
		{
			GUILayout.Label("错误：" + CheatPlugin.InitError);
		}
		GUI.color = old;
	}

	// ---- 页面渲染 ----

	private CheatMenuPage[] Pages
	{
		get
		{
			if (_pages == null)
			{
				try
				{
					_pages = CheatMenu.Build(this);
				}
				catch (Exception ex)
				{
					lock (_errLock)
					{
						_errors.Enqueue("菜单构建失败: " + ex.Message);
					}
					_pages = new CheatMenuPage[]
					{
						new CheatMenuPage
						{
							Title = "系统",
							Entries = new List<CheatMenuEntry>
							{
								new CheatMenuEntry { Label = "菜单构建失败，详见错误框", Kind = CheatEntryKind.Label }
							}
						}
					};
				}
			}
			return _pages;
		}
	}

	private List<CheatMenuEntry> CurrentEntries
	{
		get
		{
			if (_pageStack.Count > 0)
			{
				return _pageStack[_pageStack.Count - 1];
			}
			return Pages[_tab].Entries;
		}
	}

	private void DrawCurrentPage()
	{
		List<CheatMenuEntry> entries = CurrentEntries;
		int n = PureRowCount(entries);
		if (_selIndex >= n)
		{
			_selIndex = Mathf.Max(0, n - 1);
		}
		_selRect = default(Rect);
		int pureIndex = 0;
		foreach (CheatMenuEntry e in entries)
		{
			if (e.Kind == CheatEntryKind.Custom)
			{
				DrawCustomBlock(e);
			}
			else if (e.Kind == CheatEntryKind.Label)
			{
				DrawLabelRow(e);
			}
			else
			{
				DrawRow(e, pureIndex, pureIndex == _selIndex);
				pureIndex++;
			}
		}
	}

	private void DrawCustomBlock(CheatMenuEntry e)
	{
		// 头部高度随字号走（M1）：写死 20px 时字号 ×1.6 会把标题文字上下裁掉
		float hdrH = Mathf.Max(20f, RowH * 0.8f);
		Rect hdr = GUILayoutUtility.GetRect(0f, hdrH, GUILayout.ExpandWidth(true));
		TrackTop(hdr);
		DrawRect(hdr, new Color(1f, 1f, 1f, 0.04f));
		DrawLabel(new Rect(hdr.x + 8f, hdr.y, hdr.width - 16f, hdrH), e.Label, _dimStyle, C_TextDim);
		bool wasEnabled = GUI.enabled;
		if (e.RequiresServer && !CheatOps.IsServerReady)
		{
			GUI.enabled = false;
		}
		e.Draw();
		GUI.enabled = wasEnabled;
	}

	private void DrawLabelRow(CheatMenuEntry e)
	{
		// GUI.Label 会按 rect 宽度自动换行，但容器高度若固定单行，换行的第二行会溢出叠到下一项。
		// 用 CalcHeight 按实际可宽算真实行高，让长文本（如系统页"聊天命令"）完整多行显示不重叠。
		// 算行高用的可用宽度必须与实际绘制宽度一致（M2）：旧写法按 -44 算、按 -16 画，
		// 算出的行数偏多，长文本底部会留出一大块空白
		float avail = Mathf.Max(120f, _windowRect.width - 16f);
		float h = Mathf.Max(RowH, _dimStyle.CalcHeight(new GUIContent(e.Label), avail));
		Rect rect = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
		TrackTop(rect);
		DrawLabel(new Rect(rect.x + 8f, rect.y, rect.width - 16f, h), e.Label, _dimStyle, C_TextDim);
	}

	/// <summary>记录滚动内容首行的 y——所有"内容坐标"都相对它换算（F3/F4 的地基）。</summary>
	private void TrackTop(Rect r)
	{
		if (_contentTop < 0f || r.y < _contentTop)
		{
			_contentTop = r.y;
		}
	}

	private void DrawRow(CheatMenuEntry e, int pureIndex, bool isSel)
	{
		bool serverOk = !e.RequiresServer || CheatOps.IsServerReady;
		Rect rect = GUILayoutUtility.GetRect(0f, RowH, GUILayout.ExpandWidth(true));
		TrackTop(rect);
		Event ev = Event.current;
		// 键盘导航进行中时不画 hover（S5）：否则选中行和鼠标停留的行会同时亮，分不清哪个在生效
		bool hover = rect.Contains(ev.mousePosition) && !_kbNav;

		if (isSel)
		{
			DrawRect(rect, C_BgSel);
		}
		else if (hover)
		{
			DrawRect(rect, C_BgHover);
		}
		if (isSel)
		{
			_selRect = rect;
		}

		string prefix = "";
		Color textColor = serverOk ? C_Text : C_TextDim;
		if (e.Kind == CheatEntryKind.Toggle || e.Kind == CheatEntryKind.ToggleSlider)
		{
			bool on = e.Get();
			prefix = on ? "● " : "○ ";
			if (on && serverOk)
			{
				textColor = C_Accent;
			}
		}
		else if (e.IsWarning)
		{
			prefix = "⚠ ";
			if (serverOk)
			{
				textColor = C_Warn;
			}
		}

		// 滑条（Slider / ToggleSlider / SliderAction）
		bool isSliderKind = e.Kind == CheatEntryKind.Slider || e.Kind == CheatEntryKind.ToggleSlider || e.Kind == CheatEntryKind.SliderAction;

		// 右侧档位区默认 24%，Cycle 行按"最宽档位"扩宽——热键行最长档位"死亡不掉落"
		// 在 ×1.6 时 120px 远超 91px，写死 24% 就会把字切掉一半（A4）
		float rightW = rect.width * 0.24f;
		if (e.Kind == CheatEntryKind.Cycle && e.Options != null && e.Options.Length > 0)
		{
			rightW = Mathf.Clamp(CycleRightW(e.Options), rightW, rect.width * 0.46f);
		}
		Rect rightRect = new Rect(rect.x + rect.width - rightW - 8f, rect.y, rightW, RowH);

		// 标签区默认仍是 44%（保持各行滑条左端对齐），只有放不下时才向右扩张；
		// 实在放不下再补省略号 —— 宁可显示 "…" 也不要静默裁掉后半截（A3）。
		// 滑条类不扩张：滑条宽度本就被右侧区挤着，标签再长也只给 44%，否则滑条会被压没。
		string labelText = prefix + e.Label;
		float labelMax = Mathf.Max(40f, rightRect.x - rect.x - 14f);
		float labelW = rect.width * 0.44f;
		float labelNeed = _rowStyle.CalcSize(new GUIContent(labelText)).x + 4f;
		if (labelNeed > labelW && !isSliderKind)
		{
			labelW = Mathf.Min(labelNeed, labelMax);
		}
		labelW = Mathf.Min(labelW, labelMax);
		Rect labelRect = new Rect(rect.x + 8f, rect.y, labelW, RowH);
		DrawLabel(labelRect, Ellipsize(_rowStyle, labelText, labelW), _rowStyle, textColor);

		Rect sliderRect = default(Rect);
		if (isSliderKind)
		{
			float sliderX = rect.x + labelW + 14f;
			float sliderW = rightRect.x - sliderX - 10f;
			if (sliderW > 40f)
			{
				// S7-2：滑块从 14px 提到 18px，高 DPI/触屏下更容易命中；
				// 同步把皮肤里的 horizontalSlider fixedHeight 也提到 18 让轨道够宽
				float sliderH = 18f;
				if (Event.current.type == EventType.Layout && _skin.horizontalSlider.fixedHeight != sliderH)
				{
					_skin.horizontalSlider.fixedHeight = sliderH;
					_skin.horizontalSliderThumb.fixedHeight = sliderH;
				}
				sliderRect = new Rect(sliderX, rect.y + (RowH - sliderH) * 0.5f, sliderW, sliderH);
				Color old = GUI.color;
				GUI.color = serverOk ? Color.white : C_TextDim;
				float val = e.GetFloat();
				float nv = RoundToStep(GUI.HorizontalSlider(sliderRect, val, e.Min, e.Max), e.Step);
				nv = Mathf.Clamp(nv, e.Min, e.Max);
				GUI.color = old;
				if (!Mathf.Approximately(val, nv))
				{
					e.SetFloat(nv);
					if (!string.IsNullOrEmpty(e.LogKey))
					{
						_pendingLog[e.LogKey] = nv;
					}
				}
			}
		}

		// 右侧内容
		switch (e.Kind)
		{
		case CheatEntryKind.Toggle:
			DrawLabel(rightRect, e.Get() ? "ON" : "OFF", _rightStyle, e.Get() ? C_Accent : C_TextDim);
			break;
		case CheatEntryKind.Submenu:
			DrawLabel(rightRect, "›", _rightStyle, C_Accent);
			break;
		case CheatEntryKind.Cycle:
			DrawLabel(rightRect, e.Options[e.IndexGetter()], _rightStyle, serverOk ? C_Text : C_TextDim);
			break;
		case CheatEntryKind.Slider:
		case CheatEntryKind.ToggleSlider:
			DrawLabel(rightRect, e.GetFloat().ToString(e.Format) + e.Suffix, _rightStyle, serverOk ? C_Text : C_TextDim);
			break;
		case CheatEntryKind.SliderAction:
			if (hover && rightRect.Contains(ev.mousePosition))
			{
				DrawRect(rightRect, C_BgHover);
			}
			DrawLabel(rightRect, e.ActionLabel, _rightStyle, serverOk ? C_Accent : C_TextDim);
			break;
		case CheatEntryKind.Action:
			// "»" 在微软雅黑里渲染成两个紧贴的三角形，看起来像 ">>"（S2-2）。
			// 改用 "▶" 实心右指三角形，与 Submenu 的 "›" 形成清晰对比；
			// 且 Action 是"点击立即执行"的危险操作，配 C_Accent 高亮更醒目（L1-2）
			DrawLabel(rightRect, "▶", _rightStyle, C_Accent);
			break;
		}

		// 鼠标点击：选中 + 激活（滑条区点击交给原生控件，不抢）
		if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition) && serverOk)
		{
			_selIndex = pureIndex;
			bool onSlider = isSliderKind && sliderRect.height > 0f && sliderRect.Contains(ev.mousePosition);
			if (!onSlider)
			{
				switch (e.Kind)
				{
				case CheatEntryKind.Toggle:
					e.Toggle();
					LogEntry(e);
					break;
				case CheatEntryKind.Action:
					e.Action();
					LogEntry(e);
					break;
				case CheatEntryKind.Submenu:
					EnterSubmenu(e);
					break;
				case CheatEntryKind.Cycle:
				{
					int idx = e.IndexGetter();
					int ni = (idx + 1) % e.Options.Length;
					e.ApplyIndex(ni);
					if (!string.IsNullOrEmpty(e.LogKey))
					{
						_pendingLog[e.LogKey] = ni;
					}
					break;
				}
				case CheatEntryKind.ToggleSlider:
					if (ev.mousePosition.x < rect.x + labelW + 14f)
					{
						e.Toggle();
						LogEntry(e);
					}
					break;
				case CheatEntryKind.SliderAction:
					if (rightRect.Contains(ev.mousePosition))
					{
						e.Action();
						LogEntry(e);
					}
					break;
				}
			}
			ev.Use();
		}
	}

	// ---- 键盘导航状态机 ----

	private static int PureRowCount(List<CheatMenuEntry> entries)
	{
		int n = 0;
		foreach (CheatMenuEntry e in entries)
		{
			if (e.Kind != CheatEntryKind.Custom && e.Kind != CheatEntryKind.Label)
			{
				n++;
			}
		}
		return n;
	}

	private CheatMenuEntry CurrentSel()
	{
		int pure = 0;
		foreach (CheatMenuEntry e in CurrentEntries)
		{
			if (e.Kind == CheatEntryKind.Custom || e.Kind == CheatEntryKind.Label)
			{
				continue;
			}
			if (pure == _selIndex)
			{
				return e;
			}
			pure++;
		}
		return null;
	}

	private void MoveSel(int delta)
	{
		int n = PureRowCount(CurrentEntries);
		if (n <= 0)
		{
			_selIndex = 0;
			return;
		}
		_selIndex = (_selIndex + delta + n) % n;
	}

	private void StepSel(int dir)
	{
		CheatMenuEntry e = CurrentSel();
		if (e == null)
		{
			return;
		}
		switch (e.Kind)
		{
		case CheatEntryKind.Slider:
		case CheatEntryKind.ToggleSlider:
		case CheatEntryKind.SliderAction:
		{
			float v = e.GetFloat();
			float nv = RoundToStep(v + e.Step * dir, e.Step);
			nv = Mathf.Clamp(nv, e.Min, e.Max);
			e.SetFloat(nv);
			if (!string.IsNullOrEmpty(e.LogKey))
			{
				_pendingLog[e.LogKey] = nv;
			}
			break;
		}
		case CheatEntryKind.Cycle:
		{
			int idx = e.IndexGetter();
			int ni = (idx + dir + e.Options.Length) % e.Options.Length;
			e.ApplyIndex(ni);
			if (!string.IsNullOrEmpty(e.LogKey))
			{
				_pendingLog[e.LogKey] = ni;
			}
			break;
		}
		case CheatEntryKind.Submenu:
			if (dir > 0)
			{
				EnterSubmenu(e);
			}
			break;
		}
	}

	private void ActivateSel()
	{
		CheatMenuEntry e = CurrentSel();
		if (e == null)
		{
			return;
		}
		if (e.RequiresServer && !CheatOps.IsServerReady)
		{
			return;
		}
		switch (e.Kind)
		{
		case CheatEntryKind.Toggle:
		case CheatEntryKind.ToggleSlider:
			e.Toggle();
			LogEntry(e);
			break;
		case CheatEntryKind.Action:
		case CheatEntryKind.SliderAction:
			e.Action();
			LogEntry(e);
			break;
		case CheatEntryKind.Submenu:
			EnterSubmenu(e);
			break;
		}
	}

	/// <summary>统一切面入口：切到 newList 前先把当前列表的 (游标,滚动) 存入记忆（pushCurrent），
	/// 再恢复 newList 的记忆位置；无记忆则归零。所有换列表的地方（进子菜单/返回/切页）都走这里。</summary>
	private void SetCurrentList(List<CheatMenuEntry> newList, bool pushCurrent)
	{
		List<CheatMenuEntry> cur = CurrentEntries;
		if (pushCurrent && cur != null && cur != newList)
		{
			_posMemory[cur] = new Vector2(_selIndex, _scroll.y);
		}
		if (_posMemory.TryGetValue(newList, out Vector2 saved))
		{
			_selIndex = Mathf.Min((int)saved.x, Mathf.Max(0, PureRowCount(newList) - 1));
			_scroll.y = saved.y;
		}
		else
		{
			_selIndex = 0;
			_scroll = Vector2.zero;
		}
	}

	private void EnterSubmenu(CheatMenuEntry e)
	{
		SetCurrentList(e.Children, pushCurrent: true);
		_pageStack.Add(e.Children);
		_crumbStack.Add(e.Label);
	}

	private void GoBack()
	{
		if (_pageStack.Count > 0)
		{
			// 先把当前（子菜单）的位置存入记忆，返回后再进能停在上次离开处
			List<CheatMenuEntry> cur = CurrentEntries;
			_posMemory[cur] = new Vector2(_selIndex, _scroll.y);
			_pageStack.RemoveAt(_pageStack.Count - 1);
			_crumbStack.RemoveAt(_crumbStack.Count - 1);
			SetCurrentList(CurrentEntries, pushCurrent: false);
		}
		else
		{
			_open = false;
			CheatOps.Log("panel closed by backspace");
		}
	}

	/// <summary>7/9 循环切页签（7=上一页、9=下一页），各自记忆上次位置。</summary>
	private void SwitchTab(int newTab)
	{
		CheatMenuPage[] pages = Pages;
		int t = ((newTab % pages.Length) + pages.Length) % pages.Length;
		if (t == _tab)
		{
			return;
		}
		_tab = t;
		_pageStack.Clear();
		_crumbStack.Clear();
		SetCurrentList(pages[_tab].Entries, pushCurrent: true);
	}

	private static float RoundToStep(float v, float step)
	{
		if (step <= 0f)
		{
			return v;
		}
		return Mathf.Round(v / step) * step;
	}

	private void LogEntry(CheatMenuEntry e)
	{
		if (string.IsNullOrEmpty(e.LogKey))
		{
			return;
		}
		switch (e.Kind)
		{
		case CheatEntryKind.Toggle:
		case CheatEntryKind.ToggleSlider:
			_pendingLog[e.LogKey] = e.Get() ? 1f : 0f;
			break;
		case CheatEntryKind.Cycle:
			_pendingLog[e.LogKey] = e.IndexGetter();
			break;
		case CheatEntryKind.Slider:
		case CheatEntryKind.SliderAction:
			_pendingLog[e.LogKey] = e.GetFloat();
			break;
		}
	}

	private void FlushPendingLog()
	{
		if (_pendingLog.Count < 1)
		{
			return;
		}
		// 鼠标松开或键盘抬起都 flush（M13）：旧写法只认 MouseUp，
		// 纯键盘调完滑条直接按 F9 关面板，这段操作日志就永久丢了
		EventType t = Event.current.type;
		if (t != EventType.MouseUp && t != EventType.KeyUp)
		{
			return;
		}
		foreach (KeyValuePair<string, float> pending in _pendingLog)
		{
			CheatOps.Log("op=tuner " + pending.Key + "=" + pending.Value.ToString("0.##"));
		}
		_pendingLog.Clear();
	}

	private void AutoScroll()
	{
		if (_selRect.height <= 0f || _contentTop < 0f)
		{
			return;
		}
		// F3：旧写法拿"内容坐标"的 _selRect.y 去比"窗口坐标"的 _scrollViewRect，
		// 两者差一个滚动偏移量，滚过一屏之后选中行就再也回不到视口里。
		// 统一换算到内容坐标：内容 y = 行 y − 内容首行 y
		float y = _selRect.y - _contentTop;
		float viewH = _scrollViewRect.height;
		if (y < _scroll.y)
		{
			_scroll.y = y;
		}
		else if (y + _selRect.height > _scroll.y + viewH)
		{
			_scroll.y = y + _selRect.height - viewH;
		}
		_scroll.y = Mathf.Max(0f, _scroll.y);
	}

	private void HandleKeyboardNav()
	{
		Event ev = Event.current;
		if (ev.type != EventType.KeyDown)
		{
			return;
		}
		if (ChatManager.IsTyping)
		{
			return;
		}
		// 焦点在自家输入框里时，所有导航/快捷键一律让位给打字。
		// 名单必须和 IsOurInputFocused() 完全一致——之前这里只写了 3 个，
		// 漏了 tm_loop/tm_interval/tm_money，导致在这些框里输数字会误触发返回/切页
		if (IsOurInputFocused())
		{
			return;
		}
		if (CheatPlugin.PatchFailed)
		{
			return;
		}
		// 有键按下就接管高亮权（S5）
		_kbNav = true;
		switch (ev.keyCode)
		{
		case KeyCode.UpArrow:
		case KeyCode.Keypad8:
			MoveSel(-1);
			ev.Use();
			break;
		case KeyCode.DownArrow:
		case KeyCode.Keypad2:
			MoveSel(1);
			ev.Use();
			break;
		case KeyCode.LeftArrow:
		case KeyCode.Keypad4:
			// UX-S7：队友页顶层（无子菜单）时 ←→ 切换选中玩家——Custom 区块本身
			// 键盘跳过，至少让"换人"能用键盘
			if (_tab == TeammateTabIndex && _pageStack.Count == 0)
			{
				CycleTeammate(-1);
				ev.Use();
				break;
			}
			StepSel(-1);
			ev.Use();
			break;
		case KeyCode.RightArrow:
		case KeyCode.Keypad6:
			if (_tab == TeammateTabIndex && _pageStack.Count == 0)
			{
				CycleTeammate(1);
				ev.Use();
				break;
			}
			StepSel(1);
			ev.Use();
			break;
		case KeyCode.Return:
		case KeyCode.KeypadEnter:
		case KeyCode.Space:
		case KeyCode.Keypad5:
			ActivateSel();
			ev.Use();
			break;
		case KeyCode.Backspace:
			// 注意：Keypad0 原先也绑在这里，但小键盘 0 是输数字的高频键，
			// 一旦焦点判定有任何一帧延迟就会把"输入 100"变成"返回"。
			// 返回只保留 Backspace，小键盘数字键全部让给输入
			GoBack();
			ev.Use();
			break;
		case KeyCode.Alpha7:
		case KeyCode.Keypad7:
			SwitchTab(_tab - 1);
			ev.Use();
			break;
		case KeyCode.Alpha9:
		case KeyCode.Keypad9:
			SwitchTab(_tab + 1);
			ev.Use();
			break;
		case KeyCode.PageUp:
			if (StepSpawnSel(-10))
			{
				ev.Use();
			}
			break;
		case KeyCode.PageDown:
			if (StepSpawnSel(10))
			{
				ev.Use();
			}
			break;
		case KeyCode.Home:
			if (StepSpawnSel(-1000000))
			{
				ev.Use();
			}
			break;
		case KeyCode.End:
			if (StepSpawnSel(1000000))
			{
				ev.Use();
			}
			break;
		}
	}

	/// <summary>生成页物品列表的键盘翻页（S12：列表原本只能鼠标点，与全局键盘导航断裂）。
	/// 只在生成页根层级生效，返回是否真的移动了游标（没动就不吞掉按键）。</summary>
	private bool StepSpawnSel(int delta)
	{
		if (_tab != SpawnTabIndex || _pageStack.Count > 0)
		{
			return false;
		}
		if (_spawnFiltered == null || _spawnFiltered.Count < 1)
		{
			return false;
		}
		int next = Mathf.Clamp(_spawnSel + delta, 0, _spawnFiltered.Count - 1);
		if (next == _spawnSel)
		{
			return false;
		}
		_spawnSel = next;
		// 列表行等距（每行 RowH），直接按索引算内容坐标即可跟滚动
		float top = _spawnSel * RowH;
		if (top < _spawnScroll.y)
		{
			_spawnScroll.y = top;
		}
		else if (top + RowH > _spawnScroll.y + _spawnListH)
		{
			_spawnScroll.y = top + RowH - _spawnListH;
		}
		_spawnScroll.y = Mathf.Max(0f, _spawnScroll.y);
		return true;
	}

	/// <summary>状态栏右侧提示的降档候选（从全到简）：放不下就换更短的一条，
	/// 而不是让后半截被静默裁掉——默认字号下那条 24 字符的提示在 380 宽窗口里本来就超宽（A1）。</summary>
	private static readonly string[] StatusHints = new string[4]
	{
		"F9 关闭 · Backspace 返回 · ↑↓/小键盘导航",
		"F9 关闭 · Backspace 返回 · ↑↓",
		"F9 关闭 · ↑↓ 导航",
		"F9 关闭"
	};

	private void DrawStatusBar()
	{
		// 高度跟随行高：×1.6 时 24px 的字塞进写死的 24f 容器会贴边（A1）
		float h = Mathf.Max(24f, RowH);
		Rect r = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
		DrawRect(r, new Color(1f, 1f, 1f, 0.03f));
		string left = "HTF-Cheat " + Version;
		float leftW = Mathf.Min(r.width * 0.5f, _dimStyle.CalcSize(new GUIContent(left)).x + 12f);
		DrawLabel(new Rect(r.x + 8f, r.y, leftW, h), left, _dimStyle, C_TextDim);
		float rightW = r.width - leftW - 16f;
		if (rightW <= 30f)
		{
			return;
		}
		string hint = StatusHints[StatusHints.Length - 1];
		for (int i = 0; i < StatusHints.Length; i++)
		{
			if (_rightStyle.CalcSize(new GUIContent(StatusHints[i])).x <= rightW)
			{
				hint = StatusHints[i];
				break;
			}
		}
		DrawLabel(new Rect(r.x + leftW + 8f, r.y, rightW, h), hint, _rightStyle, C_TextDim);
	}

	// 右下角拖拽缩放窗口（IMGUI 原生不支持，手写手柄）。
	// 事件处理与视觉绘制分离：命中判定在 DrawWindow 开头做——ScrollView 滚动条和
	// GUI.DragWindow 都按调用顺序抢鼠标事件，旧写法把手柄排在末尾，MouseDown 轮不到它。

	private Rect ResizeHandleRect => new Rect(_windowRect.width - 20f, _windowRect.height - 20f, 18f, 18f);

	private void HandleResizeInput()
	{
		Event e = Event.current;
		if (e.type == EventType.MouseDown && ResizeHandleRect.Contains(e.mousePosition))
		{
			bool wasAuto = CheatState.AutoFitHeight;
			_resizing = true;
			// 手动拖拽即视为放弃自动自适应：否则松手后高度被自适应顶回，手动缩放无法保持
			CheatState._autoFitHeight = false;
			if (wasAuto)
			{
				// 这一步以前是静默发生的，用户只能看到"高度不动了"，莫名其妙（H5）
				ShowHint("已切到手动尺寸（系统 → 显示 → 窗口自适应尺寸 可重新打开）", 3f);
			}
			e.Use();
		}
		else if (e.type == EventType.MouseDrag && _resizing)
		{
			// 只累加不落地：GUILayout.Window 回调内改尺寸会被返回值每帧覆盖（缩放失效根因），
			// 增量统一放到 OnGUI 里窗口返回之后落地
			_resizeDelta += e.delta;
			e.Use();
		}
		else if (e.type == EventType.MouseUp && _resizing)
		{
			_resizing = false;
			e.Use();
		}
	}

	private void DrawResizeHandleVisual()
	{
		// hover / 拖拽中提亮（M10）：原来鼠标移上去毫无反馈，看不出右下角能拖
		bool hot = _resizing || ResizeHandleRect.Contains(Event.current.mousePosition);
		DrawLabel(ResizeHandleRect, "◢", _dimStyle, hot ? C_Accent : C_TextDim);
	}

	// ---- CB-01 附带：官方皮肤机后门入面板（仅列带皮肤预设的物品）----

	internal void DrawSkinPrizePicker()
	{
		if (_skinItems == null)
		{
			_skinItems = CheatOps.BuildSpawnableList().FindAll((Item p) => (bool)p && (bool)p.SkinPreset && p.SkinPreset.Skins.Count > 0);
			if (_skinSel >= _skinItems.Count)
			{
				_skinSel = 0;
			}
		}
		if (_skinItems.Count < 1)
		{
			GUILayout.Label("皮肤机：未找到带皮肤的物品");
			return;
		}
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("‹", W(_skin.button, "‹", 28f)))
		{
			_skinSel = (_skinSel - 1 + _skinItems.Count) % _skinItems.Count;
		}
		Item item = _skinItems[_skinSel];
		GUILayout.Label("皮肤机必出: " + CheatOps.DisplayName(item) + " (" + (_skinSel + 1) + "/" + _skinItems.Count + ")", GUI.skin.box);
		if (GUILayout.Button("›", W(_skin.button, "›", 28f)))
		{
			_skinSel = (_skinSel + 1) % _skinItems.Count;
		}
		GUILayout.EndHorizontal();
		bool wasEnabled = GUI.enabled;
		GUI.enabled = wasEnabled && CheatOps.IsServerReady;
		if (GUILayout.Button("设定为下次老虎机必出"))
		{
			CheatOps.SetSkinPrize(item, 0);
		}
		GUI.enabled = wasEnabled;
	}

	// ---- AM-01：把面板滑条值写进官方 PlayerAimAssist 私有字段并重算派生缓存 ----

	private PlayerAimAssist AimComp
	{
		get
		{
			if ((bool)_aimComp)
			{
				return _aimComp;
			}
			Player localPlayer = Player.LocalPlayer;
			if (!(bool)localPlayer)
			{
				return null;
			}
			_aimComp = localPlayer.GetComponent<PlayerAimAssist>();
			return _aimComp;
		}
	}

	internal void PushAimSettings()
	{
		PlayerAimAssist aimComp = AimComp;
		if (!(bool)aimComp)
		{
			return;
		}
		HTF_Fields.AimMaxRotSpeed.SetValue(aimComp, CheatState.AimStrengthWish);
		HTF_Fields.AimSharpness.SetValue(aimComp, CheatState.AimSharpnessWish);
		HTF_Fields.AimCone.SetValue(aimComp, CheatState.AimConeWish);
		HTF_Fields.AimCacheSettings.Invoke(aimComp, null);
	}

	// ---- VS-03：viewbob/FOV 是游戏静态设置，关闭覆盖时必须写回原值（enter/exit 舞步同自由相机）----

	internal void ApplyViewBobOff(bool off)
	{
		PlayerCamera.SetViewBobbing(!off);
		CheatState.SetViewBobOff(off);
		CheatOps.Log("op=viewbob off=" + off);
	}

	internal void ApplyFovOverride(bool on)
	{
		CheatState.SetFovOverride(on, CheatState._fovWish);
		if (on)
		{
			if (!_fovSaved)
			{
				object value = HTF_Fields.CamOrigFov.GetValue(null);
				_savedFov = (value != null) ? (float)value : 90f;
				_fovSaved = true;
			}
			PlayerCamera.SetFOV(CheatState.FovWish);
		}
		else if (_fovSaved)
		{
			PlayerCamera.SetFOV(_savedFov);
			_fovSaved = false;
		}
		CheatOps.Log("op=fov on=" + on + " fov=" + (on ? CheatState.FovWish : _savedFov));
	}

	/// <summary>FOV 覆盖开启时拖动滑条：实时写游戏 FOV。</summary>
	internal void ApplyFovWish(float v)
	{
		bool on = CheatState._fovOverride;
		CheatState.SetFovOverride(on, v);
		if (on)
		{
			PlayerCamera.SetFOV(CheatState.FovWish);
		}
	}

	/// <summary>全部复位时调用：把 viewbob/FOV 写回游戏默认（ResetAll 只清插件状态不清游戏设置）。</summary>
	internal void RestoreVisualSettings()
	{
		if (CheatState._viewBobOff)
		{
			ApplyViewBobOff(false);
		}
		if (CheatState._fovOverride)
		{
			ApplyFovOverride(false);
		}
	}

	// ---- FS-13 免费给饵 / 装备任意饵 ----

	internal void DrawBaitPicker()
	{
		if (_baitNames == null)
		{
			_baitNames = CheatOps.BaitNames();
			if (_baitSel >= _baitNames.Count)
			{
				_baitSel = 0;
			}
		}
		GUILayout.Label("免费给饵（主机）：");
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("‹", W(_skin.button, "‹", 28f)) && _baitNames.Count > 0)
		{
			_baitSel = (_baitSel - 1 + _baitNames.Count) % _baitNames.Count;
		}
		GUILayout.Label((_baitNames.Count > 0) ? (_baitNames[_baitSel] + "  (" + (_baitSel + 1) + "/" + _baitNames.Count + ")") : "(空)", GUI.skin.box);
		if (GUILayout.Button("›", W(_skin.button, "›", 28f)) && _baitNames.Count > 0)
		{
			_baitSel = (_baitSel + 1) % _baitNames.Count;
		}
		GUILayout.EndHorizontal();
		bool wasEnabled = GUI.enabled;
		GUI.enabled = wasEnabled && CheatOps.IsServerReady;
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("+20 个") && _baitSel >= 1)
		{
			CheatOps.GiveBait(_baitSel, 20);
		}
		if (GUILayout.Button("装备") && _baitSel >= 1)
		{
			CheatOps.EquipBait(_baitSel);
		}
		GUILayout.Label((_baitSel == 0) ? "（默认饵无库存概念）" : "", GUILayout.Width(150f));
		GUILayout.EndHorizontal();
		GUI.enabled = wasEnabled;
	}

	internal void DrawForcedCatchPicker()
	{
		if (_fishPrefabs == null)
		{
			_fishPrefabs = CheatOps.AllCreaturePrefabs();
			_fishNames = new string[_fishPrefabs.Count];
			for (int i = 0; i < _fishPrefabs.Count; i++)
			{
				string text = CheatOps.DisplayName(_fishPrefabs[i]);
				if (_fishPrefabs[i].BossType != BossType.None)
				{
					text = "* " + text;
				}
				_fishNames[i] = text;
			}
		}
		GUILayout.Label("强制渔获选择（主机）：");
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("‹", W(_skin.button, "‹", 28f)) && _fishPrefabs.Count > 0)
		{
			_fishSel = (_fishSel - 1 + _fishPrefabs.Count) % _fishPrefabs.Count;
			CheatState.SetForcedCatch(_fishPrefabs[_fishSel], CheatState._useForcedCatch);
			CheatOps.Log("op=tuner forceprefab=" + _fishPrefabs[_fishSel].name);
		}
		GUILayout.Label((_fishPrefabs.Count > 0) ? (_fishNames[_fishSel] + "  (" + (_fishSel + 1) + "/" + _fishPrefabs.Count + ")") : "(空)", GUI.skin.box);
		if (GUILayout.Button("›", W(_skin.button, "›", 28f)) && _fishPrefabs.Count > 0)
		{
			_fishSel = (_fishSel + 1) % _fishPrefabs.Count;
			CheatState.SetForcedCatch(_fishPrefabs[_fishSel], CheatState._useForcedCatch);
			CheatOps.Log("op=tuner forceprefab=" + _fishPrefabs[_fishSel].name);
		}
		GUILayout.EndHorizontal();
		ToggleRow("启用强制渔获", () => CheatState._useForcedCatch, delegate
		{
			Creature creature = ((_fishSel >= 0 && _fishSel < _fishPrefabs.Count) ? _fishPrefabs[_fishSel] : null);
			CheatState.SetForcedCatch(creature, !CheatState._useForcedCatch);
			CheatOps.Log("op=tuner forcecatch=" + CheatState._useForcedCatch);
		}, null);
	}

	// ---- IT-01 手持物品属性编辑 + ST-01 复制（经济边界测试用）----

	private float _heldWeightWish = 1f;

	private float _heldCookWish = 1f;

	private float _heldBetWish = 1f;

	private int _dupCountWish = 1;

	internal void DrawHeldItemEditor()
	{
		GUILayout.Label("手持物品编辑（先手里拿好东西）：");
		GUILayout.Label(CheatOps.HeldSummary());
		GUILayout.BeginHorizontal();
		GUILayout.Label("售价:", W(_skin.label, "售价:", 40f));
		GUI.SetNextControlName("it_amount");
		_itemWorthText = GUILayout.TextField(_itemWorthText, GUILayout.Width(110f));
		int? worth = CheatOps.ParseAmount(_itemWorthText);
		bool wasEnabled = GUI.enabled;
		GUI.enabled = wasEnabled && worth.HasValue;
		if (GUILayout.Button("设基准价"))
		{
			CheatOps.EditHeldWorth(worth.Value);
		}
		GUI.enabled = wasEnabled;
		GUILayout.EndHorizontal();
		_heldWeightWish = Mathf.Round(GUILayout.HorizontalSlider(_heldWeightWish, 0.05f, 10f) * 100f) / 100f;
		if (GUILayout.Button("设重量系数 ×" + _heldWeightWish.ToString("0.##")))
		{
			CheatOps.EditHeldWeight(_heldWeightWish);
		}
		_heldCookWish = Mathf.Round(GUILayout.HorizontalSlider(_heldCookWish, 0f, 2f) * 20f) / 20f;
		if (GUILayout.Button("设熟度 " + _heldCookWish.ToString("0.#") + "（0生/1熟/2焦）"))
		{
			CheatOps.EditHeldCookness(_heldCookWish);
		}
		_heldBetWish = Mathf.Round(GUILayout.HorizontalSlider(_heldBetWish, 0.01f, 50f) * 100f) / 100f;
		if (GUILayout.Button("设赌注倍率 ×" + _heldBetWish.ToString("0.##")))
		{
			CheatOps.EditHeldBetting(_heldBetWish);
		}
		GUILayout.Space(4f);
		GUILayout.BeginHorizontal();
		GUILayout.Label("复制份数:", W(_skin.label, "复制份数:", 64f));
		_dupCountWish = Mathf.RoundToInt(GUILayout.HorizontalSlider((float)_dupCountWish, 1f, 10f));
		GUILayout.Label(_dupCountWish.ToString(), W(_skin.label, "10", 24f));
		if (GUILayout.Button("复制手持物（真副本）"))
		{
			CheatOps.DuplicateHeldItem(_dupCountWish);
		}
		GUILayout.EndHorizontal();
	}

	internal void DrawQuestPicker()
	{
		if (GUILayout.Button("刷新任务物品列表") || _questItems == null)
		{
			_questItems = CheatOps.BuildQuestItemList();
			_questNames = new string[_questItems.Count];
			for (int i = 0; i < _questItems.Count; i++)
			{
				_questNames[i] = CheatOps.DisplayName(_questItems[i]);
			}
			if (_questSel >= _questItems.Count)
			{
				_questSel = 0;
			}
		}
		if (_questItems.Count < 1)
		{
			GUILayout.Label("本岛未找到任务物品");
			return;
		}
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("‹", W(_skin.button, "‹", 28f)))
		{
			_questSel = (_questSel - 1 + _questItems.Count) % _questItems.Count;
		}
		GUILayout.Label(_questNames[_questSel] + "  (" + (_questSel + 1) + "/" + _questItems.Count + ")", GUI.skin.box);
		if (GUILayout.Button("›", W(_skin.button, "›", 28f)))
		{
			_questSel = (_questSel + 1) % _questItems.Count;
		}
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("给一个") && _questSel >= 0 && _questSel < _questItems.Count)
		{
			CheatOps.GiveQuestItem(_questSel);
		}
		if (GUILayout.Button("全部都给"))
		{
			CheatOps.GiveQuestItem(-1);
		}
		GUILayout.EndHorizontal();
	}

	// ---- TP-01 扩展：玩家选择（传到 / 拉来共用游标）----

	internal void DrawPlayerTpRow()
	{
		List<Player> players = PlayerManager.Players;
		int count = (players != null) ? players.Count : 0;
		if (_playerSel >= count)
		{
			_playerSel = Mathf.Max(0, count - 1);
		}
		GUILayout.Label("玩家传送：");
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("‹", W(_skin.button, "‹", 28f)) && count > 0)
		{
			_playerSel = (_playerSel - 1 + count) % count;
		}
		// 与队友页一致做截断：Steam 昵称可以很长（含 emoji），不截断会把下拉框撑变形（A13）
		string label = (count > 0)
			? ("[" + _playerSel + "] " + PlayerOps.TruncateName(players[_playerSel].SteamName))
			: "(房间无人)";
		GUILayout.Label(label, GUI.skin.box);
		if (GUILayout.Button("›", W(_skin.button, "›", 28f)) && count > 0)
		{
			_playerSel = (_playerSel + 1) % count;
		}
		GUILayout.EndHorizontal();
		if (count < 2)
		{
			GUILayout.Label("（单人房无可传送对象）");
			return;
		}
		if (GUILayout.Button("传到他身边"))
		{
			CheatOps.CmdTeleport(new string[2] { "player", _playerSel.ToString() });
		}
		if (GUILayout.Button("拉到身边（主机）"))
		{
			CheatOps.CmdTeleport(new string[2] { "pull", _playerSel.ToString() });
		}
	}

	// ---- MN-01 金钱操作（主机；数额支持 1000 / 5k / 2.5m 后缀）----

	internal void DrawMoneyOps()
	{
		GUILayout.Label("金钱操作（主机）：当前 " + CheatOps.ReadMoney().ToString("N0"));
		GUILayout.BeginHorizontal();
		GUILayout.Label("数额:", W(_skin.label, "数额:", 40f));
		GUI.SetNextControlName("mn_amount");
		_moneyText = GUILayout.TextField(_moneyText, GUILayout.Width(110f));
		int? amount = CheatOps.ParseAmount(_moneyText);
		bool usable = amount.HasValue && amount.Value != 0;
		bool wasEnabled = GUI.enabled;
		GUI.enabled = wasEnabled && usable;
		if (GUILayout.Button("加", W(_skin.button, "加", 36f)))
		{
			CheatOps.AddMoneyOp(amount.Value);
		}
		if (GUILayout.Button("减", W(_skin.button, "减", 36f)))
		{
			CheatOps.RemoveMoneyOp(amount.Value);
		}
		if (GUILayout.Button("设为", W(_skin.button, "设为", 44f)))
		{
			CheatOps.SetMoneyOp(amount.Value);
		}
		GUI.enabled = wasEnabled;
		GUILayout.EndHorizontal();
	}

	// ---- SP-01 全量生成浏览器：搜索 + 分类 + 数量 + 熟化/闪光变体 ----
	// 生成页专用布局（DrawWindow 里 _tab==生成 时走这里）：直生 Boss 快捷行 + 顶部控件 +
	// 列表滚动区（页面自己的滚动，不嵌套外层）+ 底部生成按钮固定可见。

	private void DrawSpawnPageFull()
	{
		float pageTop = GUILayoutUtility.GetLastRect().yMax;
		// DrawSpawnBrowser 是无参委托（Custom(string, Action)），正推测量需要 pageTop，
		// 这里写入字段、DrawSpawnBrowser 读取（Layout/Repaint 同帧同值，无两阶段差异）
		_spawnPageTop = pageTop;
		List<CheatMenuEntry> entries = CurrentEntries;
		int pureIndex = 0;
		foreach (CheatMenuEntry e in entries)
		{
			if (e.Kind == CheatEntryKind.Custom)
			{
				continue; // 物品生成浏览器单独处理
			}
			// F2-2：生成页有两个独立选中状态（_selIndex 管直生行、_spawnSel 管物品列表），
			// 两个用同一紫色高亮会让用户分不清键盘当前控制哪个列表。
			// 生成页根层级只让物品列表亮选中态，直生行只跟 hover/键盘可达，不画高亮
			DrawRow(e, pureIndex, false);
			pureIndex++;
		}
		// S5-2：直生行和"刷新生成物列表"按钮之间加个 dim 分隔/标题，避免被当成第 6 项
		GUILayout.Space(4f);
		// 高度跟随字号：写死 18f 时 ×1.6 的 21px 字会被上下裁掉（A8）
		float secH = Mathf.Max(18f, _dimStyle.fontSize * 1.4f);
		Rect secRect = GUILayoutUtility.GetRect(0f, secH, GUILayout.ExpandWidth(true));
		DrawLabel(new Rect(secRect.x + 8f, secRect.y, secRect.width - 16f, secH), "—— 物品生成浏览器 ——", _dimStyle, C_TextDim);
		DrawSpawnBrowser();
		// 同样只在 Repaint 测量（生成页的高度会被 _spawnListH 用来算列表高度，
		// Layout 中间值会让列表高度和窗口高度互相追着抖）
		if (Event.current.type == EventType.Repaint)
		{
			// 供 OnGUI 高度自适应：整页内容高（直生行 + 顶部控件 + 列表 + 底部按钮）
			_contentH = Mathf.Max(0f, GUILayoutUtility.GetLastRect().yMax - pageTop);
			// 生成页不走外层滚动区，把"整页高"当作本帧滚动区高度
			_scrollH = _contentH;
			// _spawnOtherH 已改为在 DrawSpawnBrowser 内 ScrollView 之前正推测量（Repaint），
			// 不再用 _contentH - _spawnListH 反推——反推会把帧延迟误差喂回下一帧 avail，
			// 放大 _belowBudget 的波动（抖动根因环 3，已修复）
		}
	}

	internal void DrawSpawnBrowser()
	{
		// pageTop 由 DrawSpawnPageFull 在调用前写入 _spawnPageTop（本方法是无参委托，
		// 签名被 CheatMenuModel 的 Custom(string, Action) 锁死，不能加参）
		float pageTop = _spawnPageTop;
		if (GUILayout.Button("刷新生成物列表") || _spawnList == null)
		{
			_spawnList = CheatOps.BuildSpawnableList();
			_spawnFiltered = null;
			if (_spawnSel >= _spawnList.Count)
			{
				_spawnSel = 0;
			}
		}
		if (_spawnList.Count < 1)
		{
			GUILayout.Label("生成物注册表为空");
			return;
		}
		GUILayout.BeginHorizontal();
		// S6-2：标签和输入框基线错位——给两者显式 GUILayout.Height(RowH) 统一垂直对齐，
		// 标签用 _dimStyle + MiddleLeft 让"搜索:"和输入框文字基线一致
		GUILayout.Label("搜索:", _dimStyle, W(_dimStyle, "搜索:", 40f), GUILayout.Height(RowH));
		GUI.SetNextControlName("sp_search");
		string newSearch = GUILayout.TextField(_spawnSearch, GUILayout.Height(RowH));
		if (newSearch != _spawnSearch)
		{
			_spawnSearch = newSearch;
			_spawnFiltered = null;
		}
		if (GUILayout.Button("×", W(_skin.button, "×", 28f)) && _spawnSearch.Length > 0)
		{
			_spawnSearch = "";
			_spawnFiltered = null;
		}
		GUILayout.EndHorizontal();
		// 分类 Toolbar 字号在 ApplyFontScale 里跟随全局字号（clamp 12~16），不再钉死 12px：
		// 旧写法导致"用户把字号调到 ×1.6，全菜单都大了，唯独分类行纹丝不动"（A11）
		int newCat = GUILayout.Toolbar(_spawnCat, SpawnCats);
		if (newCat != _spawnCat)
		{
			_spawnCat = newCat;
			_spawnFiltered = null;
		}
		// 正推测量"列表以外"的固定高度（直生行 + 分隔标题 + 刷新 + 搜索 + 分类）：
		// 这部分内容高度恒定，在 ScrollView 之前直接测，不再用 _contentH - _spawnListH 反推，
		// 反推会把 ScrollView 的 margin/padding 误差和 _spawnListH 的帧延迟喂回下一帧 avail，
		// 形成放大 _belowBudget 波动的反馈环（抖动根因环 3）。只在 Repaint 测，Layout 用缓存。
		if (Event.current.type == EventType.Repaint)
		{
			_spawnOtherH = Mathf.Max(0f, GUILayoutUtility.GetLastRect().yMax - pageTop);
		}
		// 过滤缓存：键=分类|搜索串，变了才全表重扫
		string filterKey = _spawnCat + "|" + _spawnSearch;
		if (_spawnFiltered == null || _spawnFilterKey != filterKey)
		{
			_spawnFilterKey = filterKey;
			_spawnFiltered = new List<int>();
			// 用 OrdinalIgnoreCase 比 ToLower 省下每次检索上百个临时字符串（L8）
			string q = _spawnSearch;
			for (int i = 0; i < _spawnList.Count; i++)
			{
				Item prefab = _spawnList[i];
				if (!prefab)
				{
					continue;
				}
				if (_spawnCat > 0 && CheatOps.ClassifySpawnable(prefab) != SpawnCats[_spawnCat])
				{
					continue;
				}
				if (q.Length > 0
					&& prefab.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
					&& CheatOps.DisplayName(prefab).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				_spawnFiltered.Add(i);
			}
			_spawnSel = 0;
			_spawnScroll = Vector2.zero;
		}
		// 列表高度在绘制前统一计算（F5）：自适应模式下跟内容走、由它驱动窗口高度；
		// 手动尺寸模式下填满窗口剩余空间。
		// 输入全部用 Layout 事件冻结的快照（_spawnInputTop/_spawnInputBelow/_spawnInputWinH）
		// + 上一帧的 _spawnOtherH/_spawnBottomH（都在 Repaint 正推测量、只写缓存）——
		// Layout/Repaint 两阶段读到的输入完全相同，算出的 _spawnListH 也完全相同，
		// BeginScrollView 两阶段高度参数严格一致（旧的"Repaint 才重算"写法 =
		// Layout 用旧值 / Repaint 用新值，底部控件跳 1~3px）。
		// 不再有"列表高喂回预算"的反推回路（预算已改为正推测量），两阶段都算也不会互相追着变。
		float otherH = _spawnOtherH + _spawnBottomH;
		float avail = MaxWindowH - _spawnInputTop - _spawnInputBelow - otherH - WINDOW_PAD;
		_spawnListH = CheatState.AutoFitHeight
			? Mathf.Clamp(_spawnFiltered.Count * RowH + 4f, 120f, Mathf.Max(120f, avail))
			: Mathf.Clamp(_spawnInputWinH - _spawnInputTop - _spawnInputBelow - otherH - WINDOW_PAD, 120f, 900f);
		if (_spawnFiltered.Count < 1)
		{
			GUILayout.Label("(无匹配项)");
			return;
		}
		// 列表滚动区：页面自己的滚动（不嵌套外层 → 消除闪烁/双滚动/底部被裁）
		_spawnScroll = GUILayout.BeginScrollView(_spawnScroll, GUILayout.Height(_spawnListH));
		DrawSpawnListRows();
		GUILayout.EndScrollView();
		// 底部固定区：数量/加工/生成按钮（浏览列表时始终可见）
		float bottomTop = GUILayoutUtility.GetLastRect().yMax; // ScrollView 底部，作底部控件测量基准
		DrawSpawnBottomControls();
		// 底部控件高度正推：画完后实测（Repaint 专用，Layout 沿用缓存）。早退分支
		// （列表空 / 无匹配项）不更新，保持旧值——反正那时没有列表高度要算。
		if (Event.current.type == EventType.Repaint)
		{
			_spawnBottomH = Mathf.Max(0f, GUILayoutUtility.GetLastRect().yMax - bottomTop);
		}
	}

	private void DrawSpawnListRows()
	{
		for (int i = 0; i < _spawnFiltered.Count; i++)
		{
			Item it = _spawnList[_spawnFiltered[i]];
			bool selItem = i == _spawnSel;
			Rect rowRect = GUILayoutUtility.GetRect(0f, RowH, GUILayout.ExpandWidth(true));
			if (selItem)
			{
				DrawRect(rowRect, C_BgSel);
			}
			// 名称占满剩余宽度，分类右对齐（行高随字号缩放，文字不再被挡/裁切）
			string name = CheatOps.DisplayName(it);
			string cls = CheatOps.ClassifySpawnable(it);
			// 分类标签宽度按内容自适应（S4-2）：写死 70px 会在窄窗口挤名称、在宽窗口浪费空间。
			// 用 CalcSize 测量实际宽度后右对齐，最大 80px 防止英文长类名（如 "weapon"）撑开
			float catW = Mathf.Clamp(_rightStyle.CalcSize(new GUIContent(cls)).x + 10f, 32f, 80f);
			float nameW = Mathf.Max(80f, rowRect.width - 8f - catW - 6f);
			DrawLabel(new Rect(rowRect.x + 8f, rowRect.y, nameW, RowH), (selItem ? "● " : "○ ") + name, _rowStyle, selItem ? C_Accent : C_Text);
			// M4-2：选中行的分类标签也用 accent 色，与未选中形成视觉对比
			DrawLabel(new Rect(rowRect.x + rowRect.width - catW - 4f, rowRect.y, catW, RowH), cls, _rightStyle, selItem ? C_Accent : C_TextDim);
			if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
			{
				_spawnSel = i;
				Event.current.Use();
			}
		}
	}

	private void DrawSpawnBottomControls()
	{
		if (_spawnFiltered == null || _spawnFiltered.Count < 1)
		{
			return;
		}
		// 过滤结果变化后游标可能越界（S13）：加一道钳制，别让索引直接炸在绘制里
		_spawnSel = Mathf.Clamp(_spawnSel, 0, _spawnFiltered.Count - 1);
		Item sel = _spawnList[_spawnFiltered[_spawnSel]];
		GUILayout.BeginHorizontal();
		GUILayout.Label("数量:", W(_skin.label, "数量:", 40f));
		_spawnCountWish = Mathf.RoundToInt(GUILayout.HorizontalSlider((float)_spawnCountWish, 1f, 25f));
		GUILayout.Label(_spawnCountWish.ToString(), W(_skin.label, "25", 30f));
		GUILayout.Space(6f);
		_spawnCook = DrawToggle(_spawnCook, "熟", 62f);
		GUILayout.Space(6f);   // S8-2：两个自绘复选框之间留点气
		_spawnDrip = DrawToggle(_spawnDrip, "闪光", 76f);
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("生成（主机）"))
		{
			CheatOps.SpawnItemVariants(sel, _spawnCountWish, _spawnCook, _spawnDrip);
		}
		Creature creature = sel.GetComponent<Creature>();
		if ((bool)creature && GUILayout.Button("设为强制渔获"))
		{
			CheatState.SetForcedCatch(sel, true);
			CheatOps.Log("op=tuner forcedCatch=" + sel.name);
		}
		GUILayout.EndHorizontal();
	}

	// ---- VS-02 自由相机：停掉本地玩家 PlayerCamera（镜头写入唯一来源）后手动驱动 CurCamera ----
	// 游戏走 InputSystem 而非旧 Input，读 Keyboard.current/Mouse.current；面板开着时不吃鼠标增量。

	private void UpdateFreeCam()
	{
		Camera cam = GameInfo.CurCamera;
		if (!(bool)cam)
		{
			return;
		}
		Transform t = cam.transform;
		UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
		if (!_open && mouse != null)
		{
			Vector2 delta = mouse.delta.ReadValue();
			_fcYaw += delta.x * 0.22f;
			_fcPitch = Mathf.Clamp(_fcPitch - delta.y * 0.22f, -89f, 89f);
		}
		t.rotation = Quaternion.Euler(_fcPitch, _fcYaw, 0f);
		UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
		if (kb == null)
		{
			return;
		}
		float speed = kb.shiftKey.isPressed ? 30f : 10f;
		Vector3 move = Vector3.zero;
		if (kb.wKey.isPressed)
		{
			move += t.forward;
		}
		if (kb.sKey.isPressed)
		{
			move -= t.forward;
		}
		if (kb.dKey.isPressed)
		{
			move += t.right;
		}
		if (kb.aKey.isPressed)
		{
			move -= t.right;
		}
		if (kb.spaceKey.isPressed)
		{
			move += Vector3.up;
		}
		if (kb.leftCtrlKey.isPressed)
		{
			move -= Vector3.up;
		}
		if (move.sqrMagnitude > 0f)
		{
			t.position += move.normalized * speed * Time.deltaTime;
		}
	}

	internal void EnterFreeCam()
	{
		Camera cam = GameInfo.CurCamera;
		Player localPlayer = Player.LocalPlayer;
		if (!(bool)cam || !(bool)localPlayer)
		{
			CheatOps.Log("freecam: 进房后才能开");
			return;
		}
		_freecamComp = localPlayer.Camera;
		if ((bool)_freecamComp)
		{
			_freecamComp.enabled = false;
		}
		Vector3 euler = cam.transform.eulerAngles;
		_fcYaw = euler.y;
		_fcPitch = (euler.x > 180f) ? (euler.x - 360f) : euler.x;
		CheatState.SetFreeCam(true);
		CheatOps.Log("op=freecam on");
	}

	internal void ExitFreeCam()
	{
		if ((bool)_freecamComp)
		{
			_freecamComp.enabled = true;
		}
		_freecamComp = null;
		CheatState.SetFreeCam(false);
		CheatOps.Log("op=freecam off");
	}

	// ---- TM-01 PlayerOps 队友操作绘制 ----

	internal void DrawTeammateList()
	{
		DrawHostBanner();
		List<Player> players = PlayerManager.Players;
		int count = (players != null) ? players.Count : 0;
		if (_teammateSel >= count)
		{
			_teammateSel = Mathf.Max(0, count - 1);
		}
		// UX-L1：自救区独立在最前——"复活自己"作用于本地，与对队友的操作语义分开
		GUILayout.BeginHorizontal();
		GUILayout.Label("自救：", _dimStyle, W(_dimStyle, "自救：", 44f));
		if (GUILayout.Button("复活自己")) { PlayerOps.ResurrectSelf(); }
		// UX-M9：最近一次击杀 30s 内可一键撤销（复活），误杀补救入口
		if (PlayerOps.CanUndoKill() && GUILayout.Button("撤销击杀")) { PlayerOps.UndoKill(); }
		GUILayout.EndHorizontal();

		GUILayout.BeginHorizontal();
		GUILayout.Label("选择队友：", W(_skin.label, "选择队友：", 70f));
		if (GUILayout.Button("‹", W(_skin.button, "‹", 28f)) && count > 0)
		{
			_teammateSel = (_teammateSel - 1 + count) % count;
		}
		// UX-M4：名字超长/emoji 时截断显示，避免撑爆下拉框
		string label = (count > 0) ? ($"[{_teammateSel}] {PlayerOps.TruncateName(players[_teammateSel].SteamName)}") : "(房间无人)";
		GUILayout.Label(label, GUI.skin.box);
		if (GUILayout.Button("›", W(_skin.button, "›", 28f)) && count > 0)
		{
			_teammateSel = (_teammateSel + 1) % count;
		}
		if (GUILayout.Button("刷新", W(_skin.button, "刷新", 50f)))
		{
			PlayerMonitor.Refresh();
		}
		GUILayout.EndHorizontal();

		PlayerInfo info = PlayerMonitor.GetInfo(_teammateSel);
		if (info != null)
		{
			GUILayout.Label($"名字: {PlayerOps.TruncateName(info.SteamName)}", _dimStyle);
			// 信息行用 _dimWrapStyle（wordWrap）：血量/坐标/SteamID/效果都是超长文本，
			// 无 wordWrap 会在窗口宽度不足时横向溢出被裁掉（队友页宽度自适应缺失）。
			GUILayout.Label($"血量: {info.Hp:F0}/{info.MaxHp:F0}  距离: {info.Distance:F1}m", _dimWrapStyle);
			GUILayout.Label($"位置: ({info.Position.x:F0}, {info.Position.y:F0}, {info.Position.z:F0})", _dimWrapStyle);
			GUILayout.Label($"持有: {info.HeldItem}", _dimWrapStyle);
			GUILayout.Label($"SteamID: {info.SteamId}", _dimWrapStyle);
			// UX-S10：当前挂在目标身上的持续效果，一眼看清、避免"解除所有限制"一锅端
			string effects = "";
			if (info.IsLocked) effects += " 🔒锁定";
			if (info.IsForcedNoBuy) effects += " 🚫禁购";
			if (info.IsForcedNoPickup) effects += " 🚷禁拾取";
			if (PlayerOps.IsNoJump(info.Index)) effects += " ⛔禁跳";
			if (PlayerOps.IsNoAttack(info.Index)) effects += " ⚔禁攻";
			if (info.IsBeingSucked) effects += " 🧲吸人";
			if (PlayerOps.IsDisarm(info.Index)) effects += " 📦卸装";
			if (PlayerOps.IsSpin(info.Index)) effects += " 🌀旋转";
			if (PlayerOps.IsUpsideDown(info.Index)) effects += " 🙃倒吊";
			if (PlayerOps.IsBounce(info.Index)) effects += " 🏀弹球";
			GUILayout.Label("效果:" + (effects.Length > 0 ? effects : " 无"), _dimWrapStyle);
		}
		else
		{
			GUILayout.Label("无选中玩家信息", _dimStyle);
		}

		GUILayout.Space(6f);
		// UX-F4/M5：非主机时对他人操作统一置灰（传送/治疗/状态开关等全需主机），
		// 不再出现"点下去没反应"的困惑；置灰本身即提示
		bool host = CheatOps.IsServerReady;
		GUI.enabled = host;
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("传到他身边")) { PlayerOps.TeleportToPlayer(_teammateSel); }
		if (GUILayout.Button("拉到身边")) { PlayerOps.PullPlayer(_teammateSel); }
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("治疗")) { PlayerOps.HealPlayer(_teammateSel); }
		// 危险操作走二次确认：ConfirmButton 自己会画按钮并检测点击，
		// 不能再套一层 if (GUILayout.Button(...))——那样点第一下才会冒出确认按钮，逻辑全错
		ConfirmButton("kill", "杀死选中", () => PlayerOps.KillPlayer(_teammateSel), danger: true);
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("点燃")) { PlayerOps.IgnitePlayer(_teammateSel); }
		if (GUILayout.Button("中毒")) { PlayerOps.PoisonPlayer(_teammateSel); }
		GUILayout.EndHorizontal();

		GUILayout.Space(6f);
		GUILayout.Label("状态开关（选中玩家）：", _dimStyle);
		GUILayout.BeginHorizontal();
		TogglePlayerFlag("锁定", PlayerOps.IsLocked(_teammateSel), v => { if (v) PlayerOps.LockPlayer(_teammateSel); else PlayerOps.UnlockPlayer(_teammateSel); });
		TogglePlayerFlag("禁购买", PlayerOps.IsNoBuy(_teammateSel), v => PlayerOps.SetNoBuy(_teammateSel, v));
		TogglePlayerFlag("禁拾取", PlayerOps.IsNoPickup(_teammateSel), v => PlayerOps.SetNoPickup(_teammateSel, v));
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		TogglePlayerFlag("禁跳跃", PlayerOps.IsNoJump(_teammateSel), v => PlayerOps.SetNoJump(_teammateSel, v));
		TogglePlayerFlag("禁攻击", PlayerOps.IsNoAttack(_teammateSel), v => PlayerOps.SetNoAttack(_teammateSel, v));
		TogglePlayerFlag("持续卸装", PlayerOps.IsDisarm(_teammateSel), v => PlayerOps.SetDisarm(_teammateSel, v));
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("击飞")) { PlayerOps.LaunchPlayer(_teammateSel); }
		if (GUILayout.Button("解除所有限制")) { PlayerOps.ClearRestrictions(_teammateSel); }
		GUILayout.EndHorizontal();
		GUI.enabled = true;
	}

	internal void DrawTeammateBatch()
	{
		DrawHostBanner();
		// UX-S3：批量页常驻显示当前选中，防止"在列表页选的 A，切过来点执行才发现是 B"
		List<Player> batchPlayers = PlayerManager.Players;
		int batchCount = (batchPlayers != null) ? batchPlayers.Count : 0;
		if (_teammateSel >= batchCount) _teammateSel = Mathf.Max(0, batchCount - 1);
		string selName = (batchCount > 0) ? ($"[{_teammateSel}] {PlayerOps.TruncateName(batchPlayers[_teammateSel].SteamName)}") : "(无人)";
		GUILayout.Label("当前选中：" + selName, _dimStyle);

		// UX-F4/M5：批量操作全部需主机，非主机整区置灰
		bool host = CheatOps.IsServerReady;
		GUI.enabled = host;

		GUILayout.BeginHorizontal();
		GUILayout.Label("目标：", W(_skin.label, "目标：", 44f));
		// UX-L2："仅非主机"语义模糊，改名"除房主外全队"
		string[] scopes = new string[4] { "全队", "除自己", "仅选中", "除房主外全队" };
		CheatState._playerScope = GUILayout.Toolbar(CheatState._playerScope, scopes);
		GUILayout.EndHorizontal();

		// 这一行是面板里最挤的一排（标签 + 输入框 + 单位 + 3 个预设）。
		// 单位标签"次/秒"写死 18f 时，×1.6 下一个字 24px 就已经超出容器（A7）——改实测宽。
		// 预设按钮是"锦上添花"，窄窗口 + 大字号时优先牺牲它们，保住必读的标签与输入框。
		float wLoop = TextW(_skin.label, "循环：", 44f);
		float wCi = TextW(_dimStyle, "次", 18f);
		float wGap = TextW(_skin.label, "间隔：", 40f);
		float wMiao = TextW(_dimStyle, "秒", 18f);
		float presetW = TextW(_skin.button, "1", 24f) + TextW(_skin.button, "5", 24f) + TextW(_skin.button, "10", 28f);
		// 8 处控件间距按 4px 粗估，宁可估大一点早点收起预设
		bool showPresets = wLoop + wCi + wGap + wMiao + 84f + presetW + 40f <= ContentWidth;
		GUILayout.BeginHorizontal();
		GUILayout.Label("循环：", GUILayout.Width(wLoop));
		GUI.SetNextControlName("tm_loop");
		_teammateLoopText = GUILayout.TextField(_teammateLoopText, GUILayout.Width(42f));
		GUILayout.Label("次", _dimStyle, GUILayout.Width(wCi));
		// UX-M1：常用次数快捷预设，免手输
		if (showPresets)
		{
			if (GUILayout.Button("1", W(_skin.button, "1", 24f))) _teammateLoopText = "1";
			if (GUILayout.Button("5", W(_skin.button, "5", 24f))) _teammateLoopText = "5";
			if (GUILayout.Button("10", W(_skin.button, "10", 28f))) _teammateLoopText = "10";
		}
		GUILayout.Label("间隔：", _dimStyle, GUILayout.Width(wGap));
		GUI.SetNextControlName("tm_interval");
		_teammateIntervalText = GUILayout.TextField(_teammateIntervalText, GUILayout.Width(42f));
		GUILayout.Label("秒", _dimStyle, GUILayout.Width(wMiao));
		GUILayout.EndHorizontal();

		GUILayout.Space(6f);
		GUILayout.Label("物资/金钱：", _dimStyle);
		GUILayout.BeginHorizontal();
		GUILayout.Label("金额：", _dimStyle, W(_dimStyle, "金额：", 44f));
		GUI.SetNextControlName("tm_money");
		_teammateMoneyText = GUILayout.TextField(_teammateMoneyText, GUILayout.Width(80f));
		if (GUILayout.Button("给全队金钱(不可用)")) { GiveMoneyAll(); }
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("给全队物资")) { ScheduleBatch("给全队物资", () => PlayerOps.GiveItemToAll(CurrentScope(), _teammateSel, CurrentSpawnPrefab(), SpawnCount())); }
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		ConfirmButton("dropall", "全部掉落(除自己)", () => PlayerOps.DropAllExceptSelf(), danger: true);
		ConfirmButton("clearall", "全员清背包", () => PlayerOps.ClearAllInventories(CurrentScope(), _teammateSel), danger: true);
		GUILayout.EndHorizontal();

		GUILayout.Space(6f);
		GUILayout.Label("战斗：", _dimStyle);
		GUILayout.BeginHorizontal();
		ConfirmButton("killall", "杀死全队", () => PlayerOps.KillAll(CurrentScope(), _teammateSel), danger: true);
		ConfirmButton("killanimals", "杀死所有动物", () => PlayerOps.KillAllCreatures(), danger: true);
		if (GUILayout.Button("全员治疗")) { PlayerOps.HealAll(CurrentScope(), _teammateSel); }
		GUILayout.EndHorizontal();

		GUILayout.Space(6f);
		GUILayout.Label("传送：", _dimStyle);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("全员传送到我")) { PlayerOps.TeleportAllToMe(CurrentScope(), _teammateSel); }
		if (GUILayout.Button("全员传到Boss")) { PlayerOps.TeleportAllToBoss(CurrentScope(), _teammateSel); }
		GUILayout.EndHorizontal();

		GUILayout.Space(6f);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("停止所有循环")) { TaskScheduler.StopAll(); }
		GUILayout.Label($"运行中：{TaskScheduler.Tasks.Count}", _dimStyle);
		GUILayout.EndHorizontal();
		// UX-S5/M11：每个运行中任务的进度（已执行/总数 + 距下次执行秒数）+ 暂停/删除
		foreach (ScheduledTask task in TaskScheduler.Tasks)
		{
			GUILayout.BeginHorizontal();
			int done = task.TotalCycles - Mathf.Max(0, task.RemainingCycles);
			float wait = Mathf.Max(0f, task.NextRunTime - Time.unscaledTime);
			string status = task.Running ? ("下次 " + wait.ToString("F1") + "s") : "已暂停";
			GUILayout.Label($"⏱ {task.Name} {done}/{task.TotalCycles}（{status}）", _dimWrapStyle);
			if (GUILayout.Button(task.Running ? "暂停" : "继续", W(_skin.button, "暂停", 40f))) TaskScheduler.TogglePause(task.Id);
			if (GUILayout.Button("删除", W(_skin.button, "删除", 40f))) TaskScheduler.Stop(task.Id);
			GUILayout.EndHorizontal();
		}
		GUI.enabled = true;
	}

	internal void DrawTeammateTroll()
	{
		DrawHostBanner();
		GUILayout.BeginHorizontal();
		GUILayout.Label("选中玩家：", W(_skin.label, "选中玩家：", 70f));
		List<Player> players = PlayerManager.Players;
		int count = (players != null) ? players.Count : 0;
		if (_teammateSel >= count) _teammateSel = Mathf.Max(0, count - 1);
		string label = (count > 0) ? ($"[{_teammateSel}] {PlayerOps.TruncateName(players[_teammateSel].SteamName)}") : "(无人)";
		GUILayout.Label(label, GUI.skin.box);
		GUILayout.EndHorizontal();

		// UX-F4/M5：物理恶搞需主机；玩家 ESP 是纯本地渲染，留在门控外
		bool host = CheatOps.IsServerReady;
		GUI.enabled = host;
		GUILayout.Label("物理控制：", _dimStyle);
		GUILayout.BeginHorizontal();
		TogglePlayerFlag("吸选中玩家", PlayerOps.IsSucked(_teammateSel), v => PlayerOps.SetSuck(_teammateSel, v));
		TogglePlayerFlag("吸全队", CheatState._suckAllPlayers, v => PlayerOps.SetSuckAll(v));
		ConfirmButton("skytoss", "高空抛投", () => PlayerOps.SkyTossPlayer(_teammateSel), danger: true);
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		TogglePlayerFlag("旋转", PlayerOps.IsSpin(_teammateSel), v => PlayerOps.SetSpin(_teammateSel, v));
		// UX-S11：模型缩放/旋转属实验性恶搞，标注 ⚠ 防误以为普通功能
		if (GUILayout.Button("⚠缩小")) { PlayerOps.SetScale(_teammateSel, 0.5f); }
		if (GUILayout.Button("⚠放大")) { PlayerOps.SetScale(_teammateSel, 2f); }
		if (GUILayout.Button("恢复大小")) { PlayerOps.ResetScale(_teammateSel); }
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		TogglePlayerFlag("倒吊", PlayerOps.IsUpsideDown(_teammateSel), v => PlayerOps.SetUpsideDown(_teammateSel, v));
		TogglePlayerFlag("弹球", PlayerOps.IsBounce(_teammateSel), v => PlayerOps.SetBounce(_teammateSel, v));
		GUILayout.EndHorizontal();
		GUI.enabled = true;

		GUILayout.Space(6f);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("玩家 ESP")) { CheatState._playerEspOn = !CheatState._playerEspOn; }
		GUILayout.Label(CheatState._playerEspOn ? "ON" : "OFF", _dimStyle);
		GUILayout.EndHorizontal();
	}

	internal void DrawTeammateRoom()
	{
		DrawHostBanner();
		GUILayout.Label("房间工具：", _dimStyle);
		bool host = CheatOps.IsServerReady;
		GUI.enabled = host;
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("复制房间号")) { PlayerOps.CopyRoomCode(); }
		ConfirmButton("kick", "踢人", () => PlayerOps.KickPlayer(_teammateSel), danger: true);
		ConfirmButton("ban", "封禁", () => PlayerOps.BanPlayer(_teammateSel), danger: true);
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("切换昼夜")) { PlayerOps.ToggleDayNight(); }
		if (GUILayout.Button("晴天")) { PlayerOps.SetWeather(0); }
		if (GUILayout.Button("下雨")) { PlayerOps.SetWeather(1); }
		GUILayout.EndHorizontal();
		GUI.enabled = true;

		GUILayout.Space(8f);
		GUILayout.Label("运行日志：", _dimStyle);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("复制全部")) { GUIUtility.systemCopyBuffer = OperationLog.CopyFiltered(OperationLog.Categories[_teammateLogFilter]); }
		if (GUILayout.Button("清空")) { OperationLog.Clear(); }
		GUILayout.Label("过滤：", _dimStyle, W(_dimStyle, "过滤：", 44f));
		_teammateLogFilter = GUILayout.Toolbar(_teammateLogFilter, OperationLog.Categories);
		GUILayout.EndHorizontal();

		// 写死 160f 时：字号一大可见行数骤减，且这段固定高度不参与窗口自适应（H6）。
		// 改成 6 行行高（×1 时 = 156，与原值几乎一致），跟着字号一起长。
		_teammateLogScroll = GUILayout.BeginScrollView(_teammateLogScroll, GUILayout.Height(RowH * 6f));
		string filter = OperationLog.Categories[_teammateLogFilter];
		for (int i = OperationLog.Entries.Count - 1; i >= 0; i--)
		{
			LogEntry e = OperationLog.Entries[i];
			if (filter != "全部" && e.Category != filter) continue;
			Color oldC = GUI.color;
			// UX-S12：Danger 级操作（击杀/封禁/清背包）用预建暗红底样式高亮
			if (e.Level == LogLevel.Danger)
			{
				GUILayout.Label($"[{OperationLog.FormatTime(e.Time)}] [{e.Category}] {e.Message}", _logDangerStyle);
			}
			else
			{
				GUI.color = e.Level == LogLevel.Warn ? C_Warn : C_TextDim;
				GUILayout.Label($"[{OperationLog.FormatTime(e.Time)}] [{e.Category}] {e.Message}", _dimWrapStyle);
			}
			GUI.color = oldC;
		}
		GUILayout.EndScrollView();
	}

	// ---- PlayerOps 辅助方法 ----

	private void ConfirmButton(string key, string label, Action action, bool danger = false)
	{
		if (_confirmUntil.TryGetValue(key, out float until) && Time.unscaledTime < until)
		{
			// 确认态（UX-F3/L6）：显示剩余秒数 + 显式 ✕ 取消按钮，不再"只能等超时"。
			// 倒计时结束自动退回普通态，用户重新点即重新开始确认。
			GUILayout.BeginHorizontal();
			if (GUILayout.Button($"确认？({Mathf.CeilToInt(until - Time.unscaledTime)}s)"))
			{
				_confirmUntil.Remove(key);
				action?.Invoke();
				// UX-M8：确认类操作执行后按设置自动关菜单，方便立刻看游戏内效果
				if (CheatState._closeMenuAfterAction)
				{
					_open = false;
				}
			}
			if (GUILayout.Button("✕", W(_skin.button, "✕", 26f)))
			{
				_confirmUntil.Remove(key);
			}
			GUILayout.EndHorizontal();
		}
		else
		{
			// UX-S4：危险操作按钮文字染红，与安全操作拉开视觉层级
			Color old = GUI.contentColor;
			if (danger)
			{
				GUI.contentColor = new Color(1f, 0.5f, 0.5f);
			}
			if (GUILayout.Button(label))
			{
				_confirmUntil[key] = Time.unscaledTime + 3f;
			}
			GUI.contentColor = old;
		}
	}

	private void TogglePlayerFlag(string label, bool on, Action<bool> set)
	{
		if (GUILayout.Button((on ? "● " : "○ ") + label))
		{
			set(!on);
		}
	}

	/// <summary>UX-F4：非主机时队友页顶部显示权限横幅，替代"一页灰按钮却不知道为什么"。</summary>
	private void DrawHostBanner()
	{
		if (CheatOps.IsServerReady)
		{
			return;
		}
		Color c = GUI.color;
		GUI.color = C_Warn;
		GUILayout.Label("⚠ 当前非主机：以下操作大多需要房主权限", _dimStyle);
		GUI.color = c;
	}

	private PlayerOps.TargetScope CurrentScope()
	{
		return (PlayerOps.TargetScope)Mathf.Clamp(CheatState._playerScope, 0, 3);
	}

	private int ParseLoopCount()
	{
		int n;
		if (!int.TryParse(_teammateLoopText, out n) || n < 1) n = 1;
		// UX-M2：上限 99（原 999 仍可能瞬间生成海量物体炸房）；超 10 记警告提示
		return Mathf.Min(n, 99);
	}

	private float ParseLoopInterval()
	{
		float f;
		if (!float.TryParse(_teammateIntervalText, out f) || f < 0.1f) f = 0.1f;
		return Mathf.Min(f, 10f);
	}

	private void ScheduleBatch(string name, Action action)
	{
		int cycles = ParseLoopCount();
		float interval = ParseLoopInterval();
		// UX-M2：>10 次批量操作可能卡顿，提示但不阻断（诚实告知后果）
		if (cycles > 10)
		{
			OperationLog.Add("系统", $"循环 {cycles} 次可能造成卡顿，请留意", LogLevel.Warn);
		}
		TaskScheduler.Schedule(name, action, cycles, interval);
	}

	private Item CurrentSpawnPrefab()
	{
		if (_spawnList == null || _spawnList.Count == 0 || _spawnSel < 0 || _spawnSel >= _spawnList.Count)
		{
			return null;
		}
		return _spawnList[_spawnSel];
	}

	private int SpawnCount()
	{
		return Mathf.Clamp(_spawnCountWish, 1, 99);
	}

	private void GiveMoneyAll()
	{
		int? amount = CheatOps.ParseAmount(_teammateMoneyText);
		if (!amount.HasValue)
		{
			OperationLog.Add("系统", "金钱格式错误", LogLevel.Warn);
			return;
		}
		// MoneyManager 只暴露"本地玩家"的加/减钱通道，没有按 Player 给钱的 API，
		// 这里无法真实到账——诚实提示，不假装成功（宁可明确不可用，不可误导）
		OperationLog.Add("经济", $"给全队金钱 {amount.Value}：当前版本不可用（MoneyManager 仅支持本地玩家）", LogLevel.Warn);
	}

	// ---- VS-01 ESP：纯本地 IMGUI 叠加（零 RPC 零同步），面板关着也画 ----

	private void DrawEsp()
	{
		// 玩家 ESP 独立于生物 ESP：只要任意一个开关开着就进入绘制
		// （旧写法 if (!EspOn) return 会把 _playerEspOn 一起屏蔽——开玩家 ESP 关生物 ESP 时完全不画）
		if ((!CheatState.EspOn && !CheatState._playerEspOn) || !(bool)GameInfo.CurCamera)
		{
			return;
		}
		if (CheatState.EspOn)
		{
			DrawCreatureEsp();
		}
		// PlayerOps 玩家 ESP：显示名字、血量条、距离
		if (CheatState._playerEspOn)
		{
			DrawPlayerEsp(GameInfo.CurCamera, GameInfo.CurCamera.transform.position);
		}
	}

	private void DrawCreatureEsp()
	{
		if (Time.unscaledTime >= _espNextScan)
		{
			_espNextScan = Time.unscaledTime + 0.5f;
			_espCache = new List<Creature>(UnityEngine.Object.FindObjectsOfType<Creature>());
		}
		if (_espCache == null)
		{
			return;
		}
		Camera cam = GameInfo.CurCamera;
		Vector3 camPos = cam.transform.position;
		// 先收集再按距离排序（S8）：由远到近绘制，近处标签后画才压得住远处的。
		// 旧写法按 FindObjectsOfType 的顺序直接画，近处的重要信息会被远处标签盖掉。
		_espDraw.Clear();
		_espCamPos = camPos;
		foreach (Creature creature in _espCache)
		{
			// S9：失活/已销毁的个体直接跳过——死亡动画期间组件还在，标签会残留
			if (!(bool)creature || !creature.gameObject.activeInHierarchy)
			{
				continue;
			}
			Vector3 sp = cam.WorldToScreenPoint(creature.transform.position);
			if (sp.z <= 0f)
			{
				continue;
			}
			if (Vector3.Distance(camPos, creature.transform.position) > EspMaxDist)
			{
				continue;
			}
			_espDraw.Add(creature);
		}
		if (_espCmp == null)
		{
			_espCmp = CompareEspByDistance;
		}
		_espDraw.Sort(_espCmp);
		int drawn = 0;
		foreach (Creature creature in _espDraw)
		{
			Vector3 sp = cam.WorldToScreenPoint(creature.transform.position);
			if (sp.z <= 0f)
			{
				continue;
			}
			float dist = Vector3.Distance(camPos, creature.transform.position);
			string label;
			Color color;
			bool showHp;
			if (creature.BossType != BossType.None)
			{
				label = "BOSS";
				color = Color.red;
				showHp = true;
			}
			else if (creature.IsDrip)
			{
				label = "闪光";
				color = new Color(1f, 0.84f, 0f);
				showHp = true;
			}
			else if (creature is Bird)
			{
				label = "鸟";
				color = Color.white;
				showHp = false;
			}
			else if (creature is Fish)
			{
				label = "鱼";
				color = Color.cyan;
				showHp = false;
			}
			else
			{
				label = "生物";
				color = Color.gray;
				showHp = false;
			}
			// 用暗底文字代替默认白底 GUI.Box，避免生物标签呈现为白圆角框
			string text = label + (showHp ? (" " + creature.Hp) : "") + " " + dist.ToString("0") + "m";
			// 标签尺寸按实测文字宽度给（S8）：写死 120×20 时 "BOSS 99999 100m" 会把血量和距离裁掉，
			// 字号调大后文字还会溢出上下边界
			float w = Mathf.Min(260f, _dimStyle.CalcSize(new GUIContent(text)).x + 10f);
			float h = Mathf.Max(18f, _dimStyle.fontSize + 8f);
			// WorldToScreenPoint 的 y 自底向上，OnGUI 自顶向下 → 翻转；
			// 再夹回屏幕内，靠边的生物标签才不会被裁掉一半
			float x = Mathf.Clamp(sp.x - w * 0.5f, 2f, Mathf.Max(2f, Screen.width - w - 2f));
			float y = Mathf.Clamp(Screen.height - sp.y - h * 0.5f, 2f, Mathf.Max(2f, Screen.height - h - 2f));
			Rect espRect = new Rect(x, y, w, h);
			DrawRect(espRect, new Color(0f, 0f, 0f, 0.45f));
			DrawLabel(espRect, text, _dimStyle, color);
			drawn++;
			if (drawn >= EspMaxLabels)
			{
				break;
			}
		}
	}

	private void DrawPlayerEsp(Camera cam, Vector3 camPos)
	{
		Player local = Player.LocalPlayer;
		if (!local)
		{
			return;
		}
		List<Player> all = PlayerManager.Players;
		if (all == null)
		{
			return;
		}
		List<PlayerInfo> sorted = new List<PlayerInfo>(PlayerMonitor.Infos);
		sorted.Sort((a, b) => b.Distance.CompareTo(a.Distance));
		int drawn = 0;
		foreach (PlayerInfo info in sorted)
		{
			if (info.Player == local)
			{
				continue;
			}
			if (info.Distance > EspMaxDist)
			{
				continue;
			}
			Vector3 sp = cam.WorldToScreenPoint(info.Player.transform.position + Vector3.up * 2f);
			if (sp.z <= 0f)
			{
				continue;
			}
			string text = $"{info.SteamName} {info.Hp:0}/{info.MaxHp:0}HP {info.Distance:0}m";
			float w = Mathf.Min(220f, _dimStyle.CalcSize(new GUIContent(text)).x + 10f);
			float h = Mathf.Max(34f, _dimStyle.fontSize + 24f);
			float x = Mathf.Clamp(sp.x - w * 0.5f, 2f, Mathf.Max(2f, Screen.width - w - 2f));
			float y = Mathf.Clamp(Screen.height - sp.y - h * 0.5f, 2f, Mathf.Max(2f, Screen.height - h - 2f));
			Rect r = new Rect(x, y, w, h);
			DrawRect(r, new Color(0f, 0f, 0f, 0.55f));
			DrawLabel(new Rect(r.x + 4f, r.y, r.width - 8f, h - 10f), text, _dimStyle, Color.cyan);
			float barW = r.width - 8f;
			float hpRatio = Mathf.Clamp01(info.MaxHp > 0f ? info.Hp / info.MaxHp : 0f);
			DrawRect(new Rect(r.x + 4f, r.y + h - 10f, barW, 4f), Color.red);
			DrawRect(new Rect(r.x + 4f, r.y + h - 10f, barW * hpRatio, 4f), Color.green);
			drawn++;
			if (drawn >= 16)
			{
				break;
			}
		}
	}

	/// <summary>ESP 排序：远的先画、近的后画，近处标签才盖得住远处的。</summary>
	private int CompareEspByDistance(Creature a, Creature b)
	{
		float da = Vector3.Distance(_espCamPos, a.transform.position);
		float db = Vector3.Distance(_espCamPos, b.transform.position);
		return db.CompareTo(da);
	}

	// ---- 诊断（DX-01 绑定自检：游戏更新后先看这页，逐项点名失效成员）----

	private readonly List<string> _diagLines = new List<string>();

	private float _lastDiagBuild = -10f;

	private int _diagBroken;

	internal void DrawDiagTab()
	{
		if (_diagLines.Count < 1 || Time.unscaledTime - _lastDiagBuild > 0.5f)
		{
			RebuildDiagnostics();
			_lastDiagBuild = Time.unscaledTime;
		}
		for (int i = 0; i < _diagLines.Count; i++)
		{
			string line = _diagLines[i];
			Color c = GUI.color;
			if (line.StartsWith("BROKEN"))
			{
				GUI.color = Color.red;
			}
			else if (line.StartsWith("OK"))
			{
				GUI.color = new Color(0.55f, 1f, 0.55f);
			}
			GUILayout.Label(line);
			GUI.color = c;
		}
		if (GUILayout.Button("立即刷新"))
		{
			RebuildDiagnostics();
			_lastDiagBuild = Time.unscaledTime;
		}
	}

	private void CheckLine(string name, bool ok)
	{
		if (!ok)
		{
			_diagBroken++;
		}
		_diagLines.Add((ok ? "OK     " : "BROKEN ") + name);
	}

	private void RebuildDiagnostics()
	{
		_diagLines.Clear();
		_diagBroken = 0;
		_diagLines.Add("-- 补丁绑定（HTF_Fields 缓存，null=游戏更新后成员已改名）--");
		FieldInfo[] cached = typeof(HTF_Fields).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		for (int i = 0; i < cached.Length; i++)
		{
			object v = null;
			bool err = false;
			try
			{
				v = cached[i].GetValue(null);
			}
			catch (Exception ex)
			{
				err = true;
				_diagBroken++;
				_diagLines.Add("BROKEN " + cached[i].Name + " (读取异常: " + ex.Message + ")");
				continue;
			}
			bool ok = !err && v != null;
			if (!ok)
			{
				_diagBroken++;
			}
			_diagLines.Add((ok ? "OK     " : "BROKEN ") + cached[i].Name);
		}
		_diagLines.Add("");
		_diagLines.Add("-- 门控（DC-01）--");
		CheckLine("ClientSettings.CheatsEnabled=true", ClientSettings.CheatsEnabled);
		_diagLines.Add(CheatOps.DiagDevModeLine());
		_diagLines.Add("");
		_diagLines.Add("-- 世界绑定 --");
		bool serverReady = CheatOps.IsServerReady;
		CheckLine("Server.Instance 主机就绪", serverReady);
		CheckLine("Player.LocalPlayer 存在", (bool)Player.LocalPlayer);
		CheckLine("GameInfo.CurCamera 存在", (bool)GameInfo.CurCamera);
		CheckLine("GameInfo.CheatQuestItem 存在", (bool)GameInfo.CheatQuestItem);
		_diagLines.Add("");
		_diagLines.Add("-- 静态镜像 --");
		try
		{
			Creature boss = BossManager.Boss;
			CheckLine("BossManager.Boss 可读" + ((bool)boss ? (" -> " + CheatOps.DisplayName(boss)) : "（当前无 Boss）"), true);
			CheckLine("BossManager.IsImmortal=" + BossManager.IsImmortal, true);
		}
		catch (Exception ex)
		{
			_diagBroken++;
			_diagLines.Add("BROKEN BossManager 静态镜像 (读取异常: " + ex.Message + ")");
		}
		int spawnables = CheatOps.CountSpawnables();
		CheckLine("GameInfo._nameToSpawnable 可枚举 (" + spawnables + " 项)", spawnables > 0);
		_diagLines.Add("");
		_diagLines.Add((_diagBroken == 0) ? "== 结论：全部绑定正常 ==" : ("== 结论：" + _diagBroken + " 项 BROKEN —— 这些补丁点已随游戏更新失效 =="));
	}

	// ---- 常显区 ----

	private string BuildBossStatus()
	{
		Creature creature = BossManager.Boss;
		if (!creature)
		{
			return "Boss：无";
		}
		string text = "?";
		Spidercrab spidercrab = creature as Spidercrab;
		if (spidercrab != null)
		{
			text = (spidercrab._inSecondPhase.Value ? "二阶段" : "一阶段");
		}
		else
		{
			Piranha piranha = creature as Piranha;
			if (piranha != null)
			{
				text = (piranha._inSecondPhase.Value ? "二阶段" : "一阶段");
			}
			else
			{
				Pufferfish pufferfish = creature as Pufferfish;
				if (pufferfish != null)
				{
					text = (pufferfish._inSecondPhase.Value ? "二阶段" : "一阶段");
				}
				else
				{
					BowheadWhale bowheadWhale = creature as BowheadWhale;
					if (bowheadWhale != null)
					{
						text = (bowheadWhale._inSecondPhase.Value ? "二阶段" : "一阶段");
					}
					else
					{
						text = "-";
					}
				}
			}
		}
		uint num = 0u;
		if (BossManager.BossLeavesTick > InstanceFinder.TimeManager.Tick)
		{
			num = BossManager.BossLeavesTick - InstanceFinder.TimeManager.Tick;
		}
		return "Boss: " + CheatOps.DisplayName(creature) + " 血量=" + creature.Hp + "/" + BossManager.BossMaxHp + (BossManager.IsImmortal ? " [无敌期]" : "") + " " + text + " 离场:" + num + "t";
	}

	private string BuildFishingStatus()
	{
		Bait bait = null;
		foreach (Bait item in Bait.BaitsUnderWater)
		{
			if ((bool)item && (bool)item.FishingRod && item.FishingRod.Holder == Player.LocalPlayer)
			{
				bait = item;
				break;
			}
		}
		if (!(bool)bait)
		{
			return "鱼饵：未入水";
		}
		if (!(bool)bait.Info)
		{
			return "鱼饵：入水中（Info 为空——bug① 观测点！)";
		}
		float num = ((bait.RandomizedCatchTime > 0f) ? Mathf.Clamp01(bait.TimeUnderWater / bait.RandomizedCatchTime) : 0f);
		return "咬钩进度 " + Mathf.RoundToInt(num * 100f) + "%  挂钩物品=" + ((bait.ServerItemOnBait != null) ? CheatOps.DisplayName(bait.ServerItemOnBait) : "空饵");
	}

	/// <summary>UX-F2 活跃效果指示器：锁定/吸人/卸装/定时任务持续生效中，切页后也能看到，防止"忘了自己开了什么"。</summary>
	private void CycleTeammate(int dir)
	{
		List<Player> players = PlayerManager.Players;
		int count = (players != null) ? players.Count : 0;
		if (count < 2)
		{
			return;
		}
		_teammateSel = ((_teammateSel + dir + count) % count + count) % count;
		PlayerMonitor.FocusedIndex = _teammateSel;
	}

	// ---- UX-F2 活跃效果指示器 ----
	// 持续生效的恶搞（锁定/吸人/卸装）和循环任务，用户切页/关菜单后容易忘记还开着。
	// 固定占一行（无效果时返回空串），状态栏常驻提醒；数字即点击跳转恶搞页的入口。
	private string BuildActiveEffectsStatus()
	{
		int locks = PlayerOps.ActiveLockCount();
		int sucks = PlayerOps.ActiveSuckCount();
		int disarms = PlayerOps.ActiveDisarmCount();
		int tasks = TaskScheduler.Tasks.Count;
		if (locks == 0 && sucks == 0 && disarms == 0 && tasks == 0)
		{
			return "";
		}
		string s = "活跃:";
		if (locks > 0)
		{
			s += " 🔒锁定×" + locks;
		}
		if (sucks > 0)
		{
			s += " 🧲吸人×" + sucks;
		}
		if (disarms > 0)
		{
			s += " 📦卸装×" + disarms;
		}
		if (tasks > 0)
		{
			s += " ⏱任务×" + tasks;
		}
		return s;
	}

	// ---- UX-S6 操作反馈 toast ----
	// 任何操作都会写 OperationLog，这里检测"新增了日志"就在状态栏短暂显示最后一条，
	// 用户执行传送/击杀/给物后立刻看到结果，不必切到日志页。
	private string BuildOpToast()
	{
		int n = OperationLog.Entries.Count;
		if (n > _lastLogCount)
		{
			_lastLogCount = n;
			_opToastText = OperationLog.Entries[n - 1].Message;
			_opToastUntil = Time.unscaledTime + 1.5f;
		}
		// 面板提示优先于操作 toast：像"拖拽缩放已关闭窗口自适应"这种静默状态变更
		// 如果不借这个位置说一句话，用户只会以为高度自适应坏了（H5）
		if (_hintText.Length > 0 && Time.unscaledTime < _hintUntil)
		{
			return "⚠ " + _hintText;
		}
		if (_opToastText.Length > 0 && Time.unscaledTime < _opToastUntil)
		{
			return "→ " + _opToastText;
		}
		return "";
	}

	/// <summary>在状态区显示一条短提示（与操作 toast 共用同一行）。</summary>
	private void ShowHint(string text, float seconds = 2.5f)
	{
		_hintText = text;
		_hintUntil = Time.unscaledTime + seconds;
	}

	private void DrawErrorBox()
	{
		// 文本、高度、数量、按钮文案全部取自 UpdateErrorSnapshot 的 Layout 快照：
		// 画多少条、每条多高、按钮多长，Layout 与 Repaint 两阶段读到同一份 → 控件计数恒等。
		// 旧写法每条一律 Height(RowH*2) —— 超过 2 行的长错误下半截直接消失（A12）。
		for (int i = 0; i < _errN; i++)
		{
			Color color = GUI.color;
			GUI.color = Color.red;
			GUILayout.Label(_errText[i], _errorStyle, GUILayout.Height(_errH[i]));
			GUI.color = color;
		}
		if (_errN < 1)
		{
			return;
		}
		// 状态区只放最近 3 条、每条最多 4 行，完整文本一律靠这个按钮导出
		int total = _errCount;
		if (GUILayout.Button("复制全部异常 (" + total + ")"))
		{
			string all;
			lock (_errLock)
			{
				all = string.Join("\n", _errors.ToArray());
			}
			GUIUtility.systemCopyBuffer = all;
		}
	}

	// ---- 控件辅助（自定义区块内部沿用）----

	/// <summary>自绘复选框（F1 配套）：内置 toggle 的复选框本就是背景图的一部分，
	/// 背景清空后勾选状态完全不可见；而补一张纯色贴图会把整行（含文字）涂成实心条。
	/// 所以手绘方框 + 实心内芯，勾选与否一眼可辨，且不依赖任何内置贴图。</summary>
	private bool DrawToggle(bool value, string label, float width)
	{
		float boxH = Mathf.Min(14f, RowH - 4f);
		// 宽度按当前字号实测：写死 62f/76f 时 ×1.6 的"闪光"两字（48px）加上复选框就顶破容器（A9）
		float w = Mathf.Max(width, boxH + 8f + _rowStyle.CalcSize(new GUIContent(label)).x + 12f);
		Rect r = GUILayoutUtility.GetRect(w, RowH, GUILayout.Width(w));
		Event ev = Event.current;
		if (r.Contains(ev.mousePosition) && !_kbNav)
		{
			DrawRect(r, C_BgHover);
		}
		Rect box = new Rect(r.x + 2f, r.y + (RowH - boxH) * 0.5f, boxH, boxH);
		DrawRect(box, new Color(0.55f, 0.60f, 0.68f, 0.95f));
		DrawRect(new Rect(box.x + 2f, box.y + 2f, box.width - 4f, box.height - 4f),
			value ? C_Accent : new Color(0.06f, 0.06f, 0.09f, 0.95f));
		DrawLabel(new Rect(r.x + boxH + 8f, r.y, r.width - boxH - 10f, RowH), label, _rowStyle, value ? C_Text : C_TextDim);
		if (ev.type == EventType.MouseDown && r.Contains(ev.mousePosition) && GUI.enabled)
		{
			value = !value;
			ev.Use();
		}
		return value;
	}

	private void ToggleRow(string label, Func<bool> get, Action toggle, string logKey)
	{
		bool value = get();
		bool flag = DrawToggle(value, label, Mathf.Max(180f, _windowRect.width - 32f));
		if (flag != value)
		{
			toggle();
			if (!string.IsNullOrEmpty(logKey))
			{
				_pendingLog[logKey] = get() ? 1f : 0f;
			}
		}
	}

	// ---- 系统页动作（由 CheatMenuModel 接线）----

	/// <summary>备份存档目录：2 秒内再点一次确认（防误触）。</summary>
	internal void ConfirmBackup()
	{
		if (Time.unscaledTime - _lastBackupPressTime < 2f)
		{
			CheatOps.BackupSaves();
			_lastBackupPressTime = -10f;
		}
		else
		{
			_lastBackupPressTime = Time.unscaledTime;
			CheatOps.Log("backup: press again within 2s to confirm");
		}
	}
}
