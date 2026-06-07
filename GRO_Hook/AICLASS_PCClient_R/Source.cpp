#include "stdafx.h"
#include <Windows.h>
#include <stdio.h>
#include "HookedFunctions.h"
#include "ZenStuff.h"

char* logFilename = "GRO_Event_Log.txt";

void OpenConsole()
{
	AllocConsole();
	freopen("conin$","r",stdin);
	freopen("conout$","w",stdout);
	freopen("conout$","w",stderr);
	HWND consoleHandle = GetConsoleWindow();
	MoveWindow(consoleHandle,1,1,680,480,1);
	printf("Console initialized.\n");
}

bool IsThisExeName(wchar_t* name)
{
	wchar_t szFileName[MAX_PATH + 1];
	GetModuleFileName(NULL, szFileName, MAX_PATH + 1);
	return wcsstr(szFileName, name) != NULL;
}

bool FileExists(char* str)
{
	DWORD dwAttrib = GetFileAttributesA(str);
	return (dwAttrib != INVALID_FILE_ATTRIBUTES && !(dwAttrib & FILE_ATTRIBUTE_DIRECTORY));
}

void StartThread(LPTHREAD_START_ROUTINE func)
{
	DWORD dwThreadId, dwThrdParam = 1;
	HANDLE hThread;
	hThread = CreateThread(NULL, 0, func, &dwThrdParam, 0, &dwThreadId);
}

void ClearFile(char* str)
{
	FILE* fp = fopen (str, "w");
	fclose(fp);
}

void LogToFile(char* str)
{
	FILE* fp = fopen(logFilename, "a+");
	fprintf(fp, str);
	fclose(fp);
}

void Log(char* str)
{
	printf("%s", str);
	LogToFile(str);
}

void DetourFireFunctions();
void DetourEventHandlerFunctions();
void DetourClassInfoDiag();
void DetourGestureDiag();
void DetourMoodFix();
void DetourDeployDiag();
void DetourCustomizeDiag();

void WriteByte(DWORD address, BYTE b)
{	
	DWORD old;
	VirtualProtect((LPVOID)address, 1, PAGE_EXECUTE_READWRITE, &old);
	*(BYTE*)address = b;
}

void WriteBuffer(DWORD address, BYTE* buff, int len)
{
	for(int i = 0; i < len; i++)
		WriteByte(address + i, buff[i]);
}

void EnableDebugScreen1()
{
	BYTE patch[] = {0x90, 0x90};
	WriteBuffer(baseAddressAI + 0x3C384, patch, 2);
	Log("Patched position for DebugScreen 1\n");
}
void EnableDebugScreen2()
{
	BYTE patch[] = {0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
	WriteBuffer(baseAddressAI + 0x1B3716, patch, 2);
	WriteBuffer(baseAddressAI + 0x1B372C, patch, 2);
	WriteBuffer(baseAddressAI + 0x1BBABC, patch, 6);
	WriteBuffer(baseAddressAI + 0x1BBAD0, patch, 6);
	Log("Patched position for DebugScreen 2\n");
}
void EnableDebugScreen3()
{
	BYTE patch[] = {0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
	WriteBuffer(baseAddressAI + 0x3BC57, patch, 6);
	Log("Patched position for DebugScreen 3\n");
}
void EnableDebugScreen4()
{
	BYTE patch[] = {0x90, 0x90};
	WriteBuffer(baseAddressAI + 0x1EC256, patch, 2);
	Log("Patched position for DebugScreen 4\n");
}
void EnableDebugScreen5()
{
	BYTE patch[] = {0x90, 0x90};
	WriteBuffer(baseAddressAI + 0xA798E, patch, 2);
	Log("Patched position for DebugScreen 5\n");
}

void Patch1()
{
	BYTE patch[] = {0xC3};
	WriteBuffer(baseAddressAI + 0xECC30, patch, 1);
	Log("Patched crash position 1\n");
}

void Patch2()
{
	BYTE patch[] = {0xC3};
	WriteBuffer(baseAddressAI + 0x1A2FB0, patch, 1);
	Log("Patched crash position 2\n");
}

void Patch3()
{
	BYTE patch[] = {0x90, 0x90};
	WriteBuffer(baseAddressAI + 0xA735F, patch, 2);
	Log("Patched keyboard input\n");
}

void Patch4()
{
	BYTE patch[] = {0xB0, 0x01, 0xC3, 0xCC, 0xCC, 0xCC, 0xCC};//mov al, 1h; ret
	WriteBuffer(baseAddressAI + 0x2A560, patch, 7);
	Log("Patched cDNAManager::bCanSendEvent\n");
}

void Patch5()
{
	BYTE patch[] = {0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
	WriteBuffer(baseAddressAI + 0xD1B55, patch, 6);
	Log("Patched AI_EntityPlayer::InitEntity server check\n");
}

void Patch6()
{
	BYTE patch[] = {0xC3, 0xCC};//return;
	WriteBuffer(baseAddressAI + 0x1D0CB0, patch, 2);
}

void Patch7()
{
	// Patch5 forces InitEntity's serializationFlags&2 (dedicated-server) block to run on the client.
	// That block calls ClassInfoRdvPC::SerializeRDVClassInfo, which asserts "Cannot find player RDV
	// class info" (ClassInfoRdvPC.cpp:1205) when the (pid,classId) wrapper entry is missing. The only
	// seeder of that entry is ClassInfoRdvPC::FetchPlayerData, gated by `if (IsServer())` -> never runs
	// on the client. NOP the `jz` right after the IsServer() check (AICLASS 0x101CDAC4) so FetchPlayerData
	// proceeds to ds_InitLists/ds_BuildClassInfoBaseValues and creates the entry -> no assert.
	BYTE patch[] = {0x90, 0x90};
	WriteBuffer(baseAddressAI + 0x1CDAC4, patch, 2);
	Log("Patched ClassInfoRdvPC::FetchPlayerData IsServer gate (client self-seeds RDV class info)\n");
}

void CreateServerPatch()
{
	/*
	push edi
	nop x53
	*/
	BYTE push_edi[] = { 0x57, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
						0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
						0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
						0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
						0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
						0x90, 0x90, 0x90, 0x90 };
	WriteBuffer(baseAddressAI + 0xE9C0, push_edi, 54);

	/*
	pop edi
	nop x6
	*/
	//relies on ZF=0 state!
	BYTE pop_edi[] = { 0x5F, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };
	WriteBuffer(baseAddressAI + 0xE9FB, pop_edi, 7);

	/*
	nop x8
	lea ecx, ds:0x0044CDC0
	call ecx
	*/
	BYTE patch[] = {0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
					0x8D, 0x0D, 0xC0, 0xCD, 0x44, 0x00,
					0xFF, 0xD1 };
	WriteBuffer(baseAddressAI + 0x116EE, patch, 16);
	Log("The glorious server patch applied\n");
}

void OnPeerConnectionPatch()
{
	//nop x52
	BYTE patch[] = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
					0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
					0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
					0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 
					0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
					0x90, 0x90 };
	WriteBuffer(baseAddressAI + 0x11326, patch, 52);
	Log("AI_NetworkManager::OnPeerConnection assertion patched\n");
}

void IsServerPatch()
{
	BYTE patch[] = { 0xB0, 0x01 };
	WriteBuffer(baseAddressAI + 0x341B, patch, 2);
	Log("Patched AI_NetworkManager::IsServer\n");
}

void GetPeerIndexPatch()
{
	BYTE patch[] = { 0x01 };
	WriteBuffer(baseAddressAI + 0x1036F, patch, 1);
	Log("Patched AI_NetworkManager::GetPeerIndex");
}


void ExportPlayerAddress()
{
	org_AI_EntityPlayer_UpdateWarning = (void(__fastcall*) (void*,void*)) DetourFunction((PBYTE)(baseAddressAI + 0xC86F0),(PBYTE)AI_EntityPlayer_UpdateWarning);
	Log("Hooked AI_EntityPlayer::UpdateWarning\n");
}

void ReplaceVelocity()
{
	DetourFunction((PBYTE)(baseAddressAI + 0x79DC0),(PBYTE)GetVelocity);
	Log("Replaced GetVelocity\n");
}

void DetourMain()
{
	char buffer[512];
	// Drop a "_debug_output_" file next to the game exe to re-enable the UI/Flash event
	// tracing (console + per-call logging). Off by default — see the gate below.
	bool debugOutput = FileExists("_debug_output_");
	if(debugOutput)
		OpenConsole();
	ClearFile(logFilename);
	Log("GRO Hook made by Warranty Voider\n");
	baseAddressAI = (DWORD)GetModuleHandleA("AICLASS_PCClient_R_org.dll");
	if(!baseAddressAI)
	{
		Log("AI DLL not found, exit...\n");
		return;
	}
	sprintf(buffer,"AI  DLL Base = 0x%08X\n\0", baseAddressAI);
	Log(buffer);
	baseAddressRDV = (DWORD)GetModuleHandleA("RDVDLL.dll");
	if(!baseAddressRDV)
	{
		Log("RDV DLL not found, exit...\n");
		return;
	}
	sprintf(buffer,"RDV DLL Base = 0x%08X\n\0", baseAddressRDV);
	Log(buffer);
	// PERF: these install ~60 detours that fopen/fprintf/fclose the log on EVERY call
	// (FIR_SetVariable*, UI_DispatchEvent, FIR_FireEvent, per-model OnEvent/OnBusEvent).
	// The Scaleform HUD fires those hundreds of times per frame -> synchronous disk I/O
	// on the UI thread cripples framerate. Tracing is a RE/debug aid, not required by the
	// emulator, so it is OFF unless a "_debug_output_" file is present.
	if(debugOutput)
	{
		DetourFireFunctions();
		DetourEventHandlerFunctions();
	}
	//EnableDebugScreen1();
	//EnableDebugScreen2();
	//EnableDebugScreen3();
	//EnableDebugScreen4();
	//EnableDebugScreen5();
	// Locomotion (walk) VALIDATION GATE. Default = Patch1 (blunt ret on sub_100ECC30): safe lobby, no walk.
	// If a "_walktest_" file exists in the game dir, install the LOCAL-PLAYER-ONLY LocomotionApply detour
	// INSTEAD (Patch1 skipped). The detour (HookedFunctions.h) runs sub_100ECC30 ONLY for the local pawn's
	// cGestureMix (*(playerAddress+0xF6C)+8) -- so it can't touch lobby/char-select characters (the old gate's
	// crash). Requires "_moodfix_" armed (it populates dword5CC). Purpose: prove running sub_100ECC30 makes the
	// body walk before building the server-side spawn-ordering fix. Delete "_walktest_" to revert to Patch1.
	if (FileExists("_walktest_"))
	{
		org_LocomotionApply = (char*(__fastcall*)(void*, void*)) DetourFunction((PBYTE)(baseAddressAI + 0xECC30), (PBYTE)LocomotionApply_guard);
		Log("WALKTEST: local-player LocomotionApply detour installed on sub_100ECC30 (Patch1 SKIPPED)\n");
	}
	else
	{
		Patch1();  //crash1 [KEPT 2026-06-06: removal CRASHED -- sub_100ECC30 ticks at EARLY spawn (state 2) BEFORE the in-match pawn's dword5CC resolves; round-driving populates it only at state 3+ (too late). Real fix = drive dword5CC population BEFORE the locomotion driver's first tick (early server-driven mood/state update / create-deploy sequencing)]  WAS-REMOVED 2026-06-06: server-side round-driving (DS sends BM 900 StartRound after ClientReady) repopulates dword5CC post-load -- in an ACTIVE ROUND, aim/move/crouch produce mood deltas -> UpdateMood re-fires -> rosace resolver succeeds (banks loaded) -> dword5CC set. Diag (_gesture_diag_+_deploydiag_, NO _moodfix_/_walktest_): descSeen=1 on every post-deploy frame, zero null-descriptor frames. sub_100ECC30 now runs natively with a valid descriptor -> real walk, no crash. Revert: remove the leading //  //crash1 (AI_EntityHumanModel anim dword5CC null-deref — client anim init-ordering)
	}
	//Patch2();  REMOVED 2026-06-06: seeded templateitems (type=4, dtype=0) for the 202 uncovered weapon-components (iid 10128-11014) that GetTemplateItemForSlot returned null for -> GetRPPrice now receives a valid tempItem, no null-deref. Revert: remove leading // (and DELETE the seeded component rows from templateitems). orig: //crash2 — RE-ENABLED: removing it regressed the client. DS reached DO Migration but the client never sent AskForSynchronize (0xA3) -> stuck "connecting to match server". The in-match-load path still hits GR5_UserItem::GetRPPrice; the data-coverage argument wasn't sufficient.
	//Patch3();  REMOVED 2026-06-06: IDA-verified COSMETIC -- SetMovementAnimation (0x10078780) only sets the move-start anim BLEND LENGTH (SetAnimationLength), never movement; the "keyboard input" label was wrong and there is no backend cause. Reverts to correct mood-gated blend selection in cActionSelectorPC::TryWalk. Revert: remove the leading //
	//Patch4();  REMOVED 2026-06-06: op-var 89 served + CLIENT-FETCHED (backend log: "Received Request OpsProtocolService MethodID=19" at lobby time, before the match/DNA-mgr ctor) -> cDNAManager::bCanSendEvent latches true on its own from GetOpVarValue(89). Revert: remove the leading //   //cDNAManager::bCanSendEvent — RE-ENABLED with Patch2 (couldn't isolate which removal caused the stall without an A/B test; restoring both to recover the deploy-screen state)
	// Patch5/6/7 DISABLED — they forced the client into the dedicated-server entity-init path,
	// which then needs server-side RDV models (SkillsModel/InventoryModel) that don't exist for the
	// emulated player -> FetchPlayerData null-deref hard crash. Core fix: let the pawn be a normal
	// client entity (serializationFlags&2 == 0) so InitEntity skips the whole DS block and the
	// ClassInfo entry is seeded by the normal 0x271 create-blob deserialize (DeserializeMemBuffers).
	//Patch5();	//AI_EntityPlayer::InitEntity server check
	//Patch6();	//cObjectHealth::SetDefaultHitPointsServer call cancel
	//Patch7();	//FetchPlayerData IsServer gate
	//CreateServerPatch();
	//OnPeerConnectionPatch();
	//IsServerPatch();
	//GetPeerIndexPatch();
	// TEMP capture-probe (NOT Patch2): detour GetRPPrice to log the null-TemplateItem ItemID + safe-return 0.0,
	// so the lobby loads enough to capture the runtime id(s) Patch2 was masking. Removed after the server seed.
	org_GetRPPrice = (double(__cdecl*)(void*,void*,int,float)) DetourFunction((PBYTE)(baseAddressAI + 0x1A2FB0), (PBYTE)GetRPPrice_guard);
	Log("Installed GetRPPrice capture-probe (TEMP; logs null-TemplateItem ItemID, safe-returns 0.0)\n");
	// TEMP spawn-crash capture-probe (read-only): hook clean DBG_ucGetFileType to log the non-GAO key that
	// cObjectManager::LoadObjectTemplate chokes on at spawn ("Loading Wrong Type ?"). Removed after server fix.
	g_aiBase = (DWORD)baseAddressAI;
	org_DBG_ucGetFileType = (BYTE(__cdecl*)(DWORD)) DetourFunction((PBYTE)(baseAddressAI + 0x3D540), (PBYTE)DBG_ucGetFileType_probe);
	org_rev_SendErrorMessage = (int(__cdecl*)(int,char*,char*,int,char*)) DetourFunction((PBYTE)(baseAddressAI + 0x13F0), (PBYTE)rev_SendErrorMessage_probe);
	org_bTestBigKeyByType = (char(__cdecl*)(int,int)) DetourFunction((PBYTE)(baseAddressAI + 0x3D970), (PBYTE)bTestBigKeyByType_probe);
	Log("Installed assert-854 + GetFileType-remember + bTestBigKeyByType(key=0) probes (TEMP)\n");
	ExportPlayerAddress();
	if(FileExists("_ci_diag_"))		//drop an empty "_ci_diag_" file in the game dir to log ClassInfo store/lookup keys
		DetourClassInfoDiag();
	if(FileExists("_gesture_diag_"))	//drop an empty "_gesture_diag_" file to log gesture/mood/dword5CC (A-pose) activity
		DetourGestureDiag();
	if(FileExists("_moodfix_"))		//drop an empty "_moodfix_" file to re-fire UpdateMood once anim banks finish loading (fixes A-pose). Integer-only hook; no logging.
		DetourMoodFix();
	if(FileExists("_deploydiag_"))	//drop an empty "_deploydiag_" file to log (on-change) the deploy-ramp gate state for the local pawn. FP-safe (fxsave/fxrstor-bracketed, integer-only path).
		DetourDeployDiag();
	if(FileExists("_customizediag_"))	//drop an empty "_customizediag_" file to log the weapon-customize store-functor lookups + GetAttachCompType compat results (splits "no attachments offered" into functor-empty vs compat-reject).
		DetourCustomizeDiag();
	if(FileExists("_rcdiag_"))		//drop an empty "_rcdiag_" file to log the LOCAL pawn's ReplicationCallback (RC index 12 = m_Mood->UpdateMood). READ-ONLY; confirms whether a server 0x98 reaches RC(12).
	{
		org_RC_diag = (char(__fastcall*)(void*, void*, int)) DetourFunction((PBYTE)(baseAddressAI + 0xCB24C), (PBYTE)RC_diag);
		Log("Hooked AI_EntityPlayer::ReplicationCallback (RC diag)\n");
	}
	//ReplaceVelocity();
	//ZEN_Init(baseAddressAI);
}

void DetourFireFunctions()
{
	org_FIR_SendEvent = (DWORD(__cdecl*) (int,char*)) DetourFunction((PBYTE)(baseAddressAI + 0xF4C20),(PBYTE)FIR_SendEvent);
	Log("Hooked AIDLL::FIR_SendEvent\n");
	org_FIR_SetVariableString = (DWORD(__cdecl*) (int,char*,char*)) DetourFunction((PBYTE)(baseAddressAI + 0xF4DC0),(PBYTE)FIR_SetVariableString);
	Log("Hooked AIDLL::FIR_SetVariableString\n");
	org_FIR_SetVariableUniString = (DWORD(__cdecl*) (int,char*,wchar_t*)) DetourFunction((PBYTE)(baseAddressAI + 0xF4F70),(PBYTE)FIR_SetVariableUniString);
	Log("Hooked AIDLL::FIR_SetVariableUniString\n");
	org_FIR_SetVariableBool = (DWORD(__cdecl*) (int,char*,bool)) DetourFunction((PBYTE)(baseAddressAI + 0xF4E50),(PBYTE)FIR_SetVariableBool);
	Log("Hooked AIDLL::FIR_SetVariableBool\n");
	org_FIR_SetVariableInt = (DWORD(__cdecl*) (int,char*,int)) DetourFunction((PBYTE)(baseAddressAI + 0xF4EE0),(PBYTE)FIR_SetVariableInt);
	Log("Hooked AIDLL::FIR_SetVariableInt\n");
	org_FIR_SetVariableFloat = (DWORD(__cdecl*) (int,char*,float)) DetourFunction((PBYTE)(baseAddressAI + 0xF4B90),(PBYTE)FIR_SetVariableFloat);
	Log("Hooked AIDLL::FIR_SetVariableFloat\n");
	org_FIR_LoadPackage = (DWORD(__cdecl*) (int)) DetourFunction((PBYTE)(baseAddressAI + 0xF4CB0),(PBYTE)FIR_LoadPackage);
	Log("Hooked AIDLL::FIR_LoadPackage\n");
	org_FIR_UnloadPackage = (DWORD(__cdecl*) (int)) DetourFunction((PBYTE)(baseAddressAI + 0xF4D40),(PBYTE)FIR_UnloadPackage);
	Log("Hooked AIDLL::FIR_UnloadPackage\n");
	org_FIR_GetPackageKeyFromBank = (DWORD(__cdecl*) (int,int)) DetourFunction((PBYTE)(baseAddressAI + 0xF5000),(PBYTE)FIR_GetPackageKeyFromBank);
	Log("Hooked AIDLL::FIR_GetPackageKeyFromBank\n");
	org_FIR_GetASDataManager = (DWORD(__cdecl*) ()) DetourFunction((PBYTE)(baseAddressAI + 0x10B590),(PBYTE)FIR_GetASDataManager);
	Log("Hooked AIDLL::FIR_GetASDataManager\n");
	org_UI_DispatchEvent = (DWORD(__fastcall*) (EventCaller*,void*,int,int,int)) DetourFunction((PBYTE)(baseAddressAI + 0x15E350),(PBYTE)UI_DispatchEvent);
	Log("Hooked AIDLL::UI_DispatchEvent\n");
	org_FIR_FireEvent = (DWORD(__fastcall*) (void*,void*)) DetourFunction((PBYTE)(baseAddressAI + 0x1079D0),(PBYTE)FIR_FireEvent);
	Log("Hooked AIDLL::FIR_FireEvent\n");
}

void DetourEventHandlerFunctions()
{
	AddHandler((DWORD*)(baseAddressAI + 0x2A7EEC), "AI_AbilityCustomizePopUp");
	AddHandler((DWORD*)(baseAddressAI + 0x2A4480), "AI_AchievementPage");
	AddHandler((DWORD*)(baseAddressAI + 0x2A771C), "AI_AdWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A7864), "AI_ArmorCustomizePopUp");
	AddHandler((DWORD*)(baseAddressAI + 0x2A47E8), "AI_AvatarSelectionWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A748C), "AI_CharacterSelectionScreen");
	AddHandler((DWORD*)(baseAddressAI + 0x2A22F8), "AI_ChatPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A33E4), "AI_DynamicMenu");
	AddHandler((DWORD*)(baseAddressAI + 0x2A75E0), "AI_ExpiredPopUp");
	AddHandler((DWORD*)(baseAddressAI + 0x2A7674), "AI_ExpiryWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A242C), "AI_FlyingText");
	AddHandler((DWORD*)(baseAddressAI + 0x2A2A5C), "AI_FriendList");
	AddHandler((DWORD*)(baseAddressAI + 0x2A1580), "AI_GlobalUI");
	AddHandler((DWORD*)(baseAddressAI + 0x2A7A34), "AI_HomePage");
	AddHandler((DWORD*)(baseAddressAI + 0x2A2524), "AI_HudChatPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A7B5C), "AI_IgnoreList");
	AddHandler((DWORD*)(baseAddressAI + 0x2A4368), "AI_InGameAchievement");
	AddHandler((DWORD*)(baseAddressAI + 0x2A4400), "AI_InGameCallSign");
	AddHandler((DWORD*)(baseAddressAI + 0x2A41E4), "AI_InGameScore");
	AddHandler((DWORD*)(baseAddressAI + 0x2A3458), "AI_InGameUI");
	AddHandler((DWORD*)(baseAddressAI + 0x2A7CC4), "AI_Inbox");
	AddHandler((DWORD*)(baseAddressAI + 0x2A2C48), "AI_InviteCheckList");
	AddHandler((DWORD*)(baseAddressAI + 0x2A5EF4), "AI_LeaderboardPage");
	AddHandler((DWORD*)(baseAddressAI + 0x2A2854), "AI_LoadingChatPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A30D8), "AI_LoadingScreen");
	AddHandler((DWORD*)(baseAddressAI + 0x2A5D58), "AI_Login");
	AddHandler((DWORD*)(baseAddressAI + 0x2A3590), "AI_MatchRewardWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A5B50), "AI_MissionWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A31B4), "AI_MultiplayerMenu");
	AddHandler((DWORD*)(baseAddressAI + 0x2A6484), "AI_OptionsAudioPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A6514), "AI_OptionsGeneralPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A65BC), "AI_OptionsKeyMappingSPPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A6784), "AI_OptionsPage");
	AddHandler((DWORD*)(baseAddressAI + 0x2A67E8), "AI_OptionsVideoPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A6894), "AI_OptionsVoipPanel");
	AddHandler((DWORD*)(baseAddressAI + 0x2A2F28), "AI_PartyWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A3678), "AI_PostGameScore");
	AddHandler((DWORD*)(baseAddressAI + 0x2A3788), "AI_PostGameSummary");
	AddHandler((DWORD*)(baseAddressAI + 0x2A2FB8), "AI_PreGameLobby");
	AddHandler((DWORD*)(baseAddressAI + 0x2A4F30), "AI_ProfilePage");
	AddHandler((DWORD*)(baseAddressAI + 0x2A1940), "AI_ProfileWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A28C8), "AI_RoomList");
	AddHandler((DWORD*)(baseAddressAI + 0x2A3038), "AI_SingleTeamPreGameLobby");
	AddHandler((DWORD*)(baseAddressAI + 0x2A801C), "AI_SocialMenuWidget");
	AddHandler((DWORD*)(baseAddressAI + 0x2A80B0), "AI_TutorialLobby");
	AddHandler((DWORD*)(baseAddressAI + 0x29FF18), "AI_UICoreManager_0");
	AddHandler((DWORD*)(baseAddressAI + 0x2A0108), "AI_UICoreManager_1");
	AddHandler((DWORD*)(baseAddressAI + 0x2A0540), "AI_UICoreManager_2");
	AddHandler((DWORD*)(baseAddressAI + 0x2A0B20), "AI_UICoreManager_3");
	AddHandler((DWORD*)(baseAddressAI + 0x2A3F28), "AI_WeaponCustomizePopUp");
}

void DetourClassInfoDiag()
{
	sprintf(buffer, "[CI] baseAddressAI=0x%08X  (GS caller RVA = caller - base)\n\0", (DWORD)baseAddressAI);
	Log(buffer);
	org_DeserializeMemBuffers_diag = (char(__fastcall*)(void*,int,int,int,void*)) DetourFunction((PBYTE)(baseAddressAI + 0x1CD9B0), (PBYTE)DeserializeMemBuffers_diag);
	Log("Hooked ClassInfoRdvPC::DeserializeMemBuffers (CI diag)\n");
	org_GetEntryFromWrapperList_diag = (DWORD*(__thiscall*)(void*,int*)) DetourFunction((PBYTE)(baseAddressAI + 0x1C8050), (PBYTE)GetEntryFromWrapperList_diag);
	Log("Hooked ClassInfoRdvPC::GetEntryFromWrapperList (CI diag)\n");
	org_GetStruct_diag = (int(__thiscall*)(void*,void*,int)) DetourFunction((PBYTE)(baseAddressAI + 0x960A0), (PBYTE)GetStruct_diag);
	Log("Hooked cMemBuffer::GetStruct (CI diag)\n");
}

void DetourGestureDiag()
{
	org_GetActionIndexFromCode_diag = (unsigned short(__fastcall*)(void*,void*,int)) DetourFunction((PBYTE)(baseAddressAI + 0xDD220), (PBYTE)GetActionIndexFromCode_diag);
	Log("Hooked cGesture::GetActionIndexFromCode (gesture diag)\n");
	org_RosaceResolve_diag = (unsigned int(__cdecl*)(int,int)) DetourFunction((PBYTE)(baseAddressAI + 0xE8070), (PBYTE)RosaceResolve_diag);
	Log("Hooked sub_100E8070 rosace-resolve (gesture diag)\n");
	org_UpdateMood_diag = (void(__fastcall*)(void*,void*)) DetourFunction((PBYTE)(baseAddressAI + 0x76E90), (PBYTE)UpdateMood_diag);
	Log("Hooked AI_EntityPlayer::UpdateMood (gesture diag)\n");
	org_GestureSetDword5CC_diag = (void(__fastcall*)(void*,void*)) DetourFunction((PBYTE)(baseAddressAI + 0xEC270), (PBYTE)GestureSetDword5CC_diag);
	Log("Hooked sub_100EC270 dword5CC-setter (gesture diag)\n");
}

void DetourMoodFix()
{
	// Detour AI_EntityPlayer::UpdateAsyncLoadVisuals; on the load-complete (state==3) transition the
	// hook clears prev-mood and re-fires UpdateMood so the rosace resolver populates dword5CC -> A-pose
	// fixed. Hook body is integer-only (no logging / no FP) to avoid clobbering anim/weapon x87/SSE state.
	org_UpdateAsyncLoadVisuals = (void(__fastcall*)(void*,void*)) DetourFunction((PBYTE)(baseAddressAI + 0xC9800), (PBYTE)UpdateAsyncLoadVisuals_moodfix);
	Log("Hooked AI_EntityPlayer::UpdateAsyncLoadVisuals (A-pose mood fix)\n");
}

void DetourDeployDiag()
{
	// [FIRE] sub-diagnostic gate: enable the per-frame local-pawn weapon/ammo read in Spawn_deploydiag only
	// when a "_firediag_" file exists (independent kill switch; delete it to drop just the [FIRE] read).
	g_fireDiagOn = FileExists("_firediag_") ? 1 : 0;
	if (g_fireDiagOn) Log("[FIRE] diag armed (_firediag_): logging local-pawn weapon component + clip rounds on change\n");
	// Detour AI_EntityPlayerAbstract::Spawn (0x100D8C20) -- per-frame local-pawn spawn tick.
	// The hook calls the original first, then (bracketed by _fxsave/_fxrstor) reads the
	// deploy-ramp gate state with integer-only code and logs it ON CHANGE only. Reveals
	// which of inpFocus / clientReady / deploy-delay is blocking SetAsSpawned.
	org_Spawn_deploydiag = (SPAWN_FN) DetourFunction((PBYTE)(baseAddressAI + 0xD8C20), (PBYTE)Spawn_deploydiag);
	Log("Hooked AI_EntityPlayerAbstract::Spawn (deploy-ramp diag)\n");
	// Locomotion-bank diag: log whether banks 100-160 bind to the body GAO (ACT_bHasBankID @0x100629B0).
	org_ACT_bHasBankID = (char(__cdecl*)(int,int)) DetourFunction((PBYTE)(baseAddressAI + 0x629B0), (PBYTE)ACT_bHasBankID_diag);
	Log("Hooked AIDLL::ACT_bHasBankID (locomotion-bank diag)\n");
}

void DetourCustomizeDiag()
{
	// [CZ] weapon-customize store-functor diag (HookedFunctions.h). Splits "no buyable attachments"
	// into functor-empty (GetFunctorsFromList {4,weaponType} MISSING/0) vs compat-reject
	// (GetAttachCompType comp -> -1). Read-only logging passthroughs.
	org_GetFunctorsFromList_diag = (DWORD(__fastcall*)(void*,void*,int)) DetourFunction((PBYTE)(baseAddressAI + 0x121B80), (PBYTE)GetFunctorsFromList_diag);
	Log("Hooked GetFunctorsFromList (customize diag)\n");
	org_GetAttachCompType_diag = (signed int(__fastcall*)(void*,void*,int)) DetourFunction((PBYTE)(baseAddressAI + 0x121D10), (PBYTE)GetAttachCompType_diag);
	Log("Hooked AI_WeaponCustomizeHelper::GetAttachCompType (customize diag)\n");
	// [CZT] template-mode diag: who forces SetUseTemplate(1) + the bUseTemplate state at slot-draw, to find
	// whether/how the editable (non-template) customize entry is reachable.
	org_SetUseTemplate_diag = (char(__fastcall*)(void*,void*,char)) DetourFunction((PBYTE)(baseAddressAI + 0x121A20), (PBYTE)SetUseTemplate_diag);
	Log("Hooked AI_WeaponCustomizeHelper::SetUseTemplate (customize diag)\n");
	org_IsDetachable_diag = (bool(__fastcall*)(void*,void*,int)) DetourFunction((PBYTE)(baseAddressAI + 0x121D90), (PBYTE)IsDetachable_diag);
	Log("Hooked AI_WeaponCustomizeHelper::IsDetachable (customize diag)\n");
}