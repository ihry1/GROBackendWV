#include "Header.h"
#include "defines.h";
#include <intrin.h>  // _ReturnAddress (GetStruct probe caller capture)
char buffer[1024];
char buffer2[1024];
char buffer3[1024];

struct AIEvent
{
  DWORD modelID;
  DWORD eventID;
  DWORD param1;
  DWORD param2;
  DWORD unk1;
};

struct EventCaller
{
  DWORD *pVMT;
  DWORD modelID;
};


DWORD (__cdecl* org_FIR_SendEvent)(int, char*);
DWORD (__cdecl* org_FIR_SetVariableString)(int, char*, char*);
DWORD (__cdecl* org_FIR_SetVariableUniString)(int, char*, wchar_t*);
DWORD (__cdecl* org_FIR_SetVariableBool)(int, char*, bool);
DWORD (__cdecl* org_FIR_SetVariableInt)(int, char*, int);
DWORD (__cdecl* org_FIR_SetVariableFloat)(int, char*, float);
DWORD (__cdecl* org_FIR_LoadPackage)(int);
DWORD (__cdecl* org_FIR_UnloadPackage)(int);
DWORD (__cdecl* org_FIR_GetPackageKeyFromBank)(int, int);
DWORD (__cdecl* org_FIR_GetASDataManager)();

DWORD (__fastcall* org_UI_DispatchEvent)(EventCaller*, void*, int, int, int);
DWORD (__fastcall* org_FIR_FireEvent)(void*, void*);
void (__fastcall* org_AI_EntityPlayer_UpdateWarning)(void*, void*);

// TEMP Patch2-replacement capture-probe (read-only diag): logs the ItemID whose TemplateItem is null
// (the runtime id GetRPPrice would crash on with Patch2 gone) and safe-returns 0.0 so the lobby loads.
// Removed once the id is seeded server-side. NOT Patch2 (the blunt ret is gone) -- this finds the real id.
double (__cdecl* org_GetRPPrice)(void* userItem, void* tempItem, int stackNb, float mult) = 0;
double __cdecl GetRPPrice_guard(void* userItem, void* tempItem, int stackNb, float mult)
{
	if (tempItem == 0)
	{
		if (userItem)
		{
			DWORD itemId = *(DWORD*)((char*)userItem + 0x10);   // GR5_UserItem.ItemID @ +0x10
			char buf[160];
			sprintf(buf, "[RPPRICE-NULL] ItemID=%u has NO TemplateItem (would crash GetRPPrice)\n", itemId);
			Log(buf);
		}
		return 0.0;
	}
	return org_GetRPPrice(userItem, tempItem, stackNb, mult);
}

// TEMP spawn-crash capture-probe (read-only diag): the "Loading Wrong Type ?" assert (errorID 0x356) is
// raised by FOUR cObjectManager fns (LoadObjectTemplate / bTestBigKeyByType / GetMaterialDuplicate /
// bTestBigKeyMaterial_0), each checking a key's type via DBG_ucGetFileType. The first probe filtered to
// LoadObjectTemplate only and caught nothing -> the spawn crash is one of the other three. So: (1) the
// DBG_ucGetFileType hook just REMEMBERS the last (key,type,caller) -- cheap, no file I/O, no DS-lag; (2) the
// rev_SendErrorMessage hook fires ONLY on the assert and logs which fn asserted + the remembered bad key.
// Read-only; removed once the bad key is fixed server-side.
BYTE (__cdecl* org_DBG_ucGetFileType)(DWORD key) = 0;
int  (__cdecl* org_rev_SendErrorMessage)(int cond, char* path, char* func, int errorID, char* msg) = 0;
DWORD g_aiBase = 0;            // baseAddressAI, set at install (for RVA math in the log; not visible in this header)
volatile DWORD g_lastFT_key = 0;
volatile DWORD g_lastFT_caller = 0;
volatile BYTE  g_lastFT_type = 0;

BYTE __cdecl DBG_ucGetFileType_probe(DWORD key)
{
	DWORD caller = (DWORD)_ReturnAddress();
	BYTE ft = org_DBG_ucGetFileType(key);
	g_lastFT_key = key;          // remember most-recent type-check so the assert hook can name the bad key
	g_lastFT_type = ft;
	g_lastFT_caller = caller;
	return ft;
}

int __cdecl rev_SendErrorMessage_probe(int cond, char* path, char* func, int errorID, char* msg)
{
	if (errorID == 0x356)        // 854 = "Loading Wrong Type ?"
	{
		DWORD caller = (DWORD)_ReturnAddress();
		char buf[320];
		sprintf(buf, "[ASSERT-854] assertFn_rva=0x%X func=\"%s\" msg=\"%s\" | lastGetFileType key=0x%08X type=0x%02X ftCaller_rva=0x%X\n",
			caller - g_aiBase, func ? func : "?", msg ? msg : "?",
			g_lastFT_key, g_lastFT_type, g_lastFT_caller - g_aiBase);
		Log(buf);
	}
	return org_rev_SendErrorMessage(cond, path, func, errorID, msg);
}

// Pin WHO passes key=0 to bTestBigKeyByType (clean entry 0x1003d970) -- the create/load path with the missing
// asset -- plus the expected asset type. caller_rva -> the function to map back to the bad create-blob field.
char (__cdecl* org_bTestBigKeyByType)(int key, int fileType) = 0;
char __cdecl bTestBigKeyByType_probe(int key, int fileType)
{
	if (key == 0)
	{
		// Walk the ebp chain + each frame's first stack arg. The UI3DLootVisualContainer-ctor frame's a0 is the
		// bad item's ItemID (Create->GetUserItemForSlot->userItem+0x10); the Create frame's a0 is the slot.
		char buf[420];
		int len = sprintf(buf, "[BTK-ZERO] expectedType=0x%X stack:", (unsigned)fileType);
		len += sprintf(buf + len, " 0x%X", ((DWORD)_ReturnAddress()) - g_aiBase);   // c1 = LoadTemplateAsync call site
		__try
		{
			DWORD ebp = (DWORD)_AddressOfReturnAddress() - 4;   // this frame's ebp
			for (int i = 0; i < 7; i++)
			{
				DWORD next = *(DWORD*)ebp;                        // caller's saved ebp
				if (next <= ebp || next > ebp + 0x40000) break;  // sane, monotonically-rising frames only
				DWORD ret = *(DWORD*)(next + 4);                 // that frame's return address
				DWORD a0  = *(DWORD*)(next + 8);                 // that frame's first stack arg (loot-ctor a0 = ItemID)
				ebp = next;
				if (ret > g_aiBase && ret < g_aiBase + 0x400000)
					len += sprintf(buf + len, " 0x%X(a0=%X)", ret - g_aiBase, a0);
				else { len += sprintf(buf + len, " [ext]"); break; }
			}
		}
		__except (1) { len += sprintf(buf + len, " <fault>"); }
		sprintf(buf + len, "\n");
		Log(buf);
		// Dump the userItem the deploy actually reads at slot 0: replicate InventoryModel::GetUserItemForSlot(0)
		// via the global @ aiBase+0x6A4590 -> vtable[0] (__thiscall). inventoryid/itemtype identify whether slot 0
		// holds the deleted phantom (inventoryid 2002 == stale client cache), a different item, or a synthesized record.
		__try
		{
			DWORD inv = *(DWORD*)(g_aiBase + 0x6A4590);
			if (inv)
			{
				DWORD vt = *(DWORD*)inv;
				void* (__thiscall* getItem)(void*, int) = (void* (__thiscall*)(void*, int))(*(DWORD*)vt);
				DWORD* ui = (DWORD*)getItem((void*)inv, 0);
				if (ui)
				{
					char ub[220];
					sprintf(ub, "[USERITEM-0] ptr=0x%08X : %08X %08X %08X %08X %08X(ItemID@+10) %08X %08X %08X\n",
						(DWORD)ui, ui[0], ui[1], ui[2], ui[3], ui[4], ui[5], ui[6], ui[7]);
					Log(ub);
				}
				else Log("[USERITEM-0] getItem(slot 0) = NULL\n");
			}
			else Log("[USERITEM-0] InventoryModel global = NULL\n");
		}
		__except (1) { Log("[USERITEM-0] <fault dumping userItem>\n"); }
	}
	return org_bTestBigKeyByType(key, fileType);
}

typedef DWORD(__fastcall* BUSEVENTHANDLER)(void*, void*, AIEvent*);
typedef DWORD(__fastcall* EVENTHANDLER)(void*, void*, DWORD, AIEvent*);

BYTE preJump[] = {0x68, 0x00, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x00, 0x00, 0xC3}; //push 0, push 0, ret

#define MAXHANDLER	100
DWORD orgEventHandler		[MAXHANDLER];
DWORD orgEventHandlerW		[MAXHANDLER];
DWORD orgBusEventHandler	[MAXHANDLER];
char* handlerName			[MAXHANDLER];
DWORD nHandler = 0;
DWORD caller = 0;
DWORD handler = 0;
DWORD baseAddressAI = 0;
DWORD baseAddressRDV = 0;
DWORD moduleSizeAI = 0;
DWORD moduleSizeRDV = 0;
DWORD temp = 0;

__declspec(naked) void GetCallerAndHandler()
{
	_asm
	{
		//Extract Handler ID from Stack
		mov eax,[esp + 0x10];
		mov handler, eax;
		//Extract Return address from Stack
		mov eax,[esp + 0x14];
		mov caller, eax;
		//save return address
		pop eax
		mov temp, eax
		//push stack arguments down, 3x
		mov eax, [esp+8]
		mov [esp+0xC], eax
		mov eax, [esp+4]
		mov [esp+8], eax
		mov eax, [esp]
		mov [esp+4], eax
		//fix esp
		pop eax;
		//fix ebp
		add ebp, 4
		//restore return address
		mov eax, temp
		push eax
		ret
	}
}

char* getCallerString()
{
	if(caller >= baseAddressAI && caller < baseAddressAI + 0x782000)
	{
		caller -= baseAddressAI;
		caller += 0x10000000;
		sprintf(buffer3,"AI  : 0x%08X\0", caller);
	}
	else if(caller >= baseAddressRDV && caller < baseAddressRDV + 0x71C000)
	{
		caller -= baseAddressRDV;
		caller += 0x10000000;
		sprintf(buffer3,"RDV : 0x%08X\0", caller);
	}
	else
		sprintf(buffer3,"UNK : 0x%08X\0", caller);
	return buffer3;
}

DWORD __fastcall BusEventHandler(void* THIS, void* EDX, AIEvent* e)
{	
	GetCallerAndHandler();
	sprintf(buffer2,"AIDLL::%s::OnBusEvent\0", handlerName[handler]);
	sprintf(buffer,"%s -> %-48s (\"%s\", 0x%08X, 0x%08X, 0x%08X, 0x%08X)\n\0", getCallerString(), buffer2, modelNames[e->modelID], e->eventID, e->param1, e->param2, e->unk1);
	Log(buffer);
	return ((BUSEVENTHANDLER)orgBusEventHandler[handler])(THIS, EDX, e);
}

DWORD __fastcall EventHandler(void* THIS, void* EDX, DWORD unk1, AIEvent* e)
{	
	GetCallerAndHandler();
	sprintf(buffer2,"AIDLL::%s::OnEvent\0", handlerName[handler]);
	sprintf(buffer,"%s -> %-48s (0x%08X, %s)\n\0", getCallerString(), buffer2, unk1, e->eventID);
	Log(buffer);
	return ((EVENTHANDLER)orgEventHandler[handler])(THIS, EDX, unk1, e);
}

DWORD __fastcall EventHandlerW(void* THIS, void* EDX, DWORD unk1, AIEvent* e)
{	
	GetCallerAndHandler();
	sprintf(buffer2,"AIDLL::%s::OnEventW\0", handlerName[handler]);
	sprintf(buffer,"%s -> %-48s (0x%08X, %s)\n\0", getCallerString(), buffer2, unk1, e->eventID);
	Log(buffer);
	return ((EVENTHANDLER)orgEventHandlerW[handler])(THIS, EDX, unk1, e);
}

void AddHandler(DWORD* pVMT, char* name)
{
	if(nHandler >= MAXHANDLER)
	{
		sprintf(buffer,"Cant add handler %s\n\0", name);
		Log(buffer);
		return;
	}
	handlerName[nHandler] = name;
	DWORD old;
	VirtualProtect(pVMT,36,PAGE_EXECUTE_READWRITE,&old);
	//detouring OnBusEvent
	orgBusEventHandler[nHandler] = pVMT[0];
	BYTE* trampolineStub = (BYTE*)calloc(11, 1);
	memcpy((void*)trampolineStub, (const void*)preJump, 11);
	*(DWORD*)(&trampolineStub[1]) = nHandler;
	*(DWORD*)(&trampolineStub[6]) = (DWORD)&BusEventHandler;
	pVMT[0] = (DWORD)trampolineStub;
	//detouring OnEvent
	orgEventHandler[nHandler] = pVMT[6];
	trampolineStub = (BYTE*)calloc(11, 1);
	memcpy((void*)trampolineStub, (const void*)preJump, 11);
	*(DWORD*)(&trampolineStub[1]) = nHandler;
	*(DWORD*)(&trampolineStub[6]) = (DWORD)&EventHandler;
	pVMT[6] = (DWORD)trampolineStub;
	//detouring OnEventW
	orgEventHandlerW[nHandler] = pVMT[7];
	trampolineStub = (BYTE*)calloc(11, 1);
	memcpy((void*)trampolineStub, (const void*)preJump, 11);
	*(DWORD*)(&trampolineStub[1]) = nHandler;
	*(DWORD*)(&trampolineStub[6]) = (DWORD)&EventHandlerW;
	pVMT[7] = (DWORD)trampolineStub;
	sprintf(buffer,"Replaced handlers OnBusEvent(0x%08X), OnEvent(0x%08X), OnEventW(0x%08X) for %s (pVMT=0x%08X)\n\0", 
		orgBusEventHandler[nHandler] - baseAddressAI + 0x10000000, 
		orgEventHandler[nHandler] - baseAddressAI + 0x10000000, 
		orgEventHandlerW[nHandler] - baseAddressAI + 0x10000000, 
		name,
		pVMT);
	Log(buffer);
	nHandler++;
}

__declspec(naked) void GetCaller()
{
	_asm
	{
		push eax;
		mov eax,[esp + 0xC];
		mov caller, eax;
		pop eax
		ret;
	}
}

__declspec(naked) void GetCaller2()
{
	_asm
	{
		push eax
		mov eax, [esp+0x10];
		mov caller, eax;
		pop eax
		ret;
	}
}

__declspec(naked) void GetCaller3()
{
	_asm
	{
		push eax
		mov eax, [esp+0x14];
		mov caller, eax;
		pop eax
		ret;
	}
}

DWORD __cdecl FIR_SendEvent(int unk, char* name)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_SendEvent                             (0x%08X, \"%s\")\n\0", getCallerString(), unk, name);
	Log(buffer);
	return org_FIR_SendEvent(unk, name);
}

DWORD __cdecl FIR_SetVariableString(int unk, char* name, char* value)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_SetVariableString                     (0x%08X, \"%s\", \"%s\")\n\0", getCallerString(), unk, name, value);
	Log(buffer);
	return org_FIR_SetVariableString(unk, name, value);
}

DWORD __cdecl FIR_SetVariableUniString(int unk, char* name, wchar_t* value)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_SetVariableUniString                  (0x%08X, \"%s\", \"%S\")\n\0", getCallerString(), unk, name, value);
	Log(buffer);
	return org_FIR_SetVariableUniString(unk, name, value);
}

DWORD __cdecl FIR_SetVariableBool(int unk, char* name, bool value)
{
	GetCaller2();
	sprintf(buffer,"%s -> AIDLL::FIR_SetVariableBool                       (0x%08X, \"%s\", \"%s\")\n\0", getCallerString(), unk, name, value ? "TRUE" : "FALSE");
	Log(buffer);
	return org_FIR_SetVariableBool(unk, name, value);
}

DWORD __cdecl FIR_SetVariableInt(int unk, char* name, int value)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_SetVariableInt                        (0x%08X, \"%s\", 0x%08X)\n\0", getCallerString(), unk, name, value);
	Log(buffer);
	return org_FIR_SetVariableInt(unk, name, value);
}

DWORD __cdecl FIR_SetVariableFloat(int unk, char* name, float value)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_SetVariableFloat                      (0x%08X, \"%s\", %f)\n\0", getCallerString(), unk, name, value);
	Log(buffer);
	return org_FIR_SetVariableFloat(unk, name, value);
}

DWORD __cdecl FIR_LoadPackage(int unk)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_LoadPackage                           (0x%08X)\n\0", getCallerString(), unk);
	Log(buffer);
	return org_FIR_LoadPackage(unk);
}

DWORD __cdecl FIR_UnloadPackage(int unk)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_UnloadPackage                         (0x%08X)\n\0", getCallerString(), unk);
	Log(buffer);
	return org_FIR_UnloadPackage(unk);
}

DWORD __cdecl FIR_GetPackageKeyFromBank(int unk1, int unk2)
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_GetPackageKeyFromBank                 (0x%08X, 0x%08X)\n\0", getCallerString(), unk1, unk2);
	Log(buffer);
	return org_FIR_GetPackageKeyFromBank(unk1, unk2);
}

void __cdecl FIR_GetASDataManager()
{
	GetCaller();
	sprintf(buffer,"%s -> AIDLL::FIR_GetASDataManager                      ()\n\0", getCallerString());
	Log(buffer);
	org_FIR_GetASDataManager();
}

DWORD __fastcall UI_DispatchEvent(EventCaller* THIS, void* EDX, int a1, int a2, int a3)
{	
	GetCaller3();
	sprintf(buffer,"%s -> AIDLL::UI_DispatchEvent                          (\"%s\", 0x%08X, 0x%08X, 0x%08X)\n\0", getCallerString(), modelNames[THIS->modelID], a1, a2, a3);
	Log(buffer);
	return org_UI_DispatchEvent(THIS, EDX, a1, a2, a3);
}

DWORD __fastcall FIR_FireEvent(void* THIS, void* EDX)
{	
	GetCaller3();
	sprintf(buffer,"%s -> AIDLL::FIR_FireEvent                             ()\n\0", getCallerString());
	Log(buffer);
	return org_FIR_FireEvent(THIS, EDX);
}

DWORD playerAddress = 0;

DWORD WINAPI StepStepStep(LPVOID lpvParam)
{
	Sleep(10000);
	DWORD* stanceFlags = (DWORD*)(playerAddress + 0x824);
	while(true)
	{
		Sleep(1000);
		*stanceFlags |= 0x40000;
	}
	return 0;
}

float* __cdecl GetVelocity(DWORD a1, DWORD a2)
{
	float result[3];
	result[0] = 0;
	result[1] = 0;
	result[2] = 100;
	return result;
}

void __fastcall AI_EntityPlayer_UpdateWarning(void* THIS, void* EDX)
{
	if(playerAddress == 0)
	{
		playerAddress = (DWORD)THIS;
		sprintf(buffer,"PlayerAddress=%08X\n\0", playerAddress);		
		FILE* fp = fopen("_playerAddress.txt", "w");
		fprintf(fp, buffer);
		fclose(fp);
		//StartThread(StepStepStep);
	}
	org_AI_EntityPlayer_UpdateWarning(THIS, EDX);
}

// --- ClassInfo wrapper-list diagnostics (installed only when a "_ci_diag_" file exists). ---
// Logs every (pid,classId) STORE (DeserializeMemBuffers) and LOOKUP (GetEntryFromWrapperList ->
// FOUND/NULL) so we can see whether the 0x271 create-blob seeds the entry the local pawn reads.
char   (__fastcall* org_DeserializeMemBuffers_diag)(void*, int, int, int, void*);
DWORD* (__thiscall* org_GetEntryFromWrapperList_diag)(void*, int*);

char __fastcall DeserializeMemBuffers_diag(void* THIS, int a2, int pid, int classId, void* memBuff)
{
	sprintf(buffer, "[CI] STORE DeserializeMemBuffers   pid=0x%08X classId=0x%02X\n\0", pid, classId & 0xFF);
	Log(buffer);
	// Dump the raw ClassInfo-slot framing from the create blob at the current read cursor.
	// cMemBuffer: Pos@+0x08, Size@+0x0C, inline data@+0x10. Shows [len0][payload0...][len1]...
	// so the actual per-slot lengths are visible (slot0 @ off0, slot1 @ off len0+1, grenade slot2 ~off 84).
	{
		int p  = *(int*)((char*)memBuff + 0x08);
		int sz = *(int*)((char*)memBuff + 0x0C);
		char* data = (char*)memBuff + 0x10;
		char hex[640]; hex[0] = 0;
		for (int k = 0; k < 190 && k < sz; k++)               // dump from blob START so field markers can be matched
			sprintf(hex + strlen(hex), "%02X ", (unsigned char)data[k]);
		sprintf(buffer, "[CI] blob[0..190] Pos=%d Size=%d: %s\n\0", p, sz, hex);
		Log(buffer);
	}
	return org_DeserializeMemBuffers_diag(THIS, a2, pid, classId, memBuff);
}

DWORD* __fastcall GetEntryFromWrapperList_diag(void* THIS, void* EDX, int* key)
{
	DWORD* result = org_GetEntryFromWrapperList_diag(THIS, key);
	sprintf(buffer, "[CI] LOOKUP GetEntryFromWrapperList pid=0x%08X classId=0x%02X -> %s\n\0", key[0], key[1] & 0xFF, result ? "FOUND" : "NULL");
	Log(buffer);
	return result;
}

// --- cMemBuffer::GetStruct probe (cBuffer.cpp:20 "invalid size!"). Logs ONLY when a read would
//     overrun: size>=0x1000 || Pos+size > Size. The buffer Size identifies the ClassInfo slot
//     (Main/Pistol=41, Grenade=42, Armor=73, Helmet=4, Ability=101, Passive=17, Boost=13, Body=56);
//     (Pos, size) pinpoints the overrunning field. cMemBuffer layout: Pos@+0x08, Size@+0x0C. ---
int (__thiscall* org_GetStruct_diag)(void*, void*, int);

int __fastcall GetStruct_diag(void* THIS, void* EDX, void* dst, int size)
{
	void* caller = _ReturnAddress();
	int pos = *(int*)((char*)THIS + 0x08);
	int sz  = *(int*)((char*)THIS + 0x0C);
	if (sz == 544 && pos < 150)   // trace EVERY read of the 544B player-create blob pre-slot region to find which field is short
	{
		sprintf(buffer, "[GS] read Pos=%d size=%d\n\0", pos, size);
		Log(buffer);
	}
	if (size >= 0x1000 || pos + size > sz)
	{
		sprintf(buffer, "[GS] INVALID size=%d Pos=%d Size=%d (over by %d) caller=0x%08X\n\0", size, pos, sz, (pos + size) - sz, (DWORD)caller);
		Log(buffer);
	}
	return org_GetStruct_diag(THIS, dst, size);
}

// ============================================================================
// GESTURE / MOOD / A-POSE DIAGNOSTICS  (installed only when a "_gesture_diag_" file exists)
// ----------------------------------------------------------------------------
// Goal: ground-truth WHY the in-match pawn is stuck in an A-pose and whether the
// network Gesture cmd (Entity_Cmd 0x28/0x29) or the mood/rosace path is the real
// driver of the active anim descriptor cGestureMix.dword5CC (@+0x5CC).
//
// Hooks (AICLASS RVAs off baseAddressAI, imagebase 0x10000000):
//   0x0DD220 cGesture::GetActionIndexFromCode(this=ecx, animID)  -> logs *this (gesture code),
//            animID, and result. The "Gesture(162) invalid user code(-1)" error is THIS func:
//            field1=*this (code), field2=animID. Confirms the 0x28 path passes animID=-1.
//   0x0E8070 sub_100E8070(rosace, gestureSet) [__cdecl] -> the rosace->gesture-descriptor resolver
//            called from UpdateMood. Return 0 == gesture not loaded/not found. KEY TIMING PROBE:
//            when does it START returning non-zero (== anim bank for that rosace is loaded)?
//   0x076E90 AI_EntityPlayer::UpdateMood(this=ecx) -> logs m_Mood currValue(+? via field) & prev.
//            Shows WHEN UpdateMood (re)fires and the mood delta it sees.
//   0x0EC270 sub_100EC270(cGestureMix* this=ecx) -> the ONLY setter of dword5CC (from list2 head).
//            Logs list2.lastIndex (V1: lastIndex@+4, entries@+0xC) and the dword5CC value it sets.
//            dword5CC@+0x5CC ; list2@+0x650. This DIRECTLY observes the A-pose fix moment.
// All hooks are read-only logging passthroughs.
// ============================================================================
unsigned short (__fastcall* org_GetActionIndexFromCode_diag)(void*, void*, int);
unsigned int   (__cdecl*    org_RosaceResolve_diag)(int, int);
void           (__fastcall* org_UpdateMood_diag)(void*, void*);
void           (__fastcall* org_GestureSetDword5CC_diag)(void*, void*);

unsigned short __fastcall GetActionIndexFromCode_diag(void* THIS, void* EDX, int animID)
{
	unsigned short res = org_GetActionIndexFromCode_diag(THIS, EDX, animID);
	// cGesture sub-object: *this(u16)=gesture code, *((u8*)this+6)=range, this[1]=base list ptr
	unsigned short code  = *(unsigned short*)THIS;
	unsigned char  range = *((unsigned char*)THIS + 6);
	sprintf(buffer, "[GES] GetActionIndexFromCode code=%u animID=%d range=%u -> %d%s\n\0",
		code, animID, range, (short)res, (res == 0xFFFF) ? "  (NOT FOUND)" : "");
	Log(buffer);
	return res;
}

// counters so we can see the transition without flooding the log
int g_rosace_lastResultZero = -1;
unsigned int __cdecl RosaceResolve_diag(int rosace, int gestureSet)
{
	unsigned int res = org_RosaceResolve_diag(rosace, gestureSet);
	// log every call during the interesting window; res==0 means the rosace's gesture isn't resolvable yet
	sprintf(buffer, "[ROS] sub_100E8070 rosace=%d set=%d -> 0x%08X%s\n\0",
		rosace, gestureSet, res, res ? "  (RESOLVED)" : "  (none/not-loaded)");
	Log(buffer);
	return res;
}

void __fastcall UpdateMood_diag(void* THIS, void* EDX)
{
	// Fires at AI_EntityHuman::InitEntity's no-op UpdateMood (caller RVA 0x89279) in BOTH the working
	// and the CRASHING native spawn -- so this is where we capture the spawn-gate state in the FAILING
	// case (the [MO] hook can't: it only fires when Order_ChangeMood(3) actually runs = gate passed).
	// serializationFlags @THIS+0x30 (&1=MASTER gates AI_EntityPlayer::InitEntity's Order_ChangeMood(3));
	// owner/playerStationID @+0x6C; AI_NetworkManager::Instance @global RVA 0x6ACBA4 -> myNetId @+0x170,
	// InternalOwnerId @+0x16B. If the native crash shows &1=0 here, the gate is why dword5CC stays null.
	void* caller = _ReturnAddress();
	DWORD sflag = *(DWORD*)((char*)THIS + 0x30);
	DWORD owner = *(DWORD*)((char*)THIS + 0x6C);
	DWORD nmInst = *(DWORD*)(baseAddressAI + 0x6ACBA4);
	DWORD myNet  = nmInst ? *(DWORD*)(nmInst + 0x170) : 0;
	DWORD intOwn = nmInst ? (*(BYTE*)(nmInst + 0x16B) ? 1u : 0u) : 0;
	DWORD gao    = *(DWORD*)((char*)THIS + 0x14);       // this->gameObject (GAO)
	DWORD mmood  = *(DWORD*)((char*)THIS + 0x824);      // m_Mood.currValue (the gameplay flag UpdateMood reads) -- should be 0xCE from LoadFrom, empirically 0
	DWORD mprev  = *(DWORD*)((char*)THIS + 0xDA8);      // mood-prev (UpdateMood delta basis)
	DWORD cr = ((DWORD)caller >= baseAddressAI && (DWORD)caller < baseAddressAI + 0x782000)
		? ((DWORD)caller - baseAddressAI + 0x10000000) : (DWORD)caller;
	sprintf(buffer, "[MOOD] UpdateMood ENTER this=0x%08X caller=0x%08X(rva0x%X) m_Mood=0x%X prev=0x%X sflag=0x%X &1=%d gameObject=0x%08X intOwn=%d\n\0",
		(DWORD)THIS, (DWORD)caller, cr, mmood, mprev, sflag, (sflag & 1), gao, intOwn);
	Log(buffer);
	org_UpdateMood_diag(THIS, EDX);
	sprintf(buffer, "[MOOD] UpdateMood LEAVE\n\0");
	Log(buffer);
}

void __fastcall GestureSetDword5CC_diag(void* THIS, void* EDX)
{
	DWORD before = *(DWORD*)((char*)THIS + 0x5CC);
	int   l2cnt  = *(int*)((char*)THIS + 0x650 + 0x4);   // list2.lastIndex
	org_GestureSetDword5CC_diag(THIS, EDX);
	DWORD after  = *(DWORD*)((char*)THIS + 0x5CC);
	if (before != after || after != 0)
	{
		sprintf(buffer, "[5CC] sub_100EC270 this=0x%08X list2.cnt=%d dword5CC: 0x%08X -> 0x%08X%s\n\0",
			(DWORD)THIS, l2cnt, before, after, (before == 0 && after != 0) ? "  *** A-POSE FIXED ***" : "");
		Log(buffer);
	}
}

// ============================================================================
// A-POSE MOOD FIX  (installed only when a "_moodfix_" file exists)  -- NON-DIAGNOSTIC
// ----------------------------------------------------------------------------
// Root cause (verified by RE + runtime capture): the active-anim descriptor
// cGestureMix.dword5CC stays null because AI_EntityHuman::InitEntity fires
// AI_EntityPlayer::UpdateMood at spawn, BEFORE the character's anim banks finish
// async-loading. UpdateMood acts only on mood DELTAS (prev-mood @ this+0xDA8 vs
// m_Mood.currValue @ this+0x824) and nothing re-fires it once the banks land, so the
// rosace resolver (which only returns non-zero with banks loaded) is never re-run for
// the already-set mood bits -> dword5CC never gets populated -> permanent A-pose.
//
// Fix: detour AI_EntityPlayer::UpdateAsyncLoadVisuals (0x100C9800). That function walks a
// load-state field at this+0x1400 through 1 -> 2 -> 3 (3 == fully loaded; verified in
// disasm: "mov dword ptr [esi+1400h], 3" at 0x100C9878). Its sole caller
// (AI_EntityPlayer::Update @0x100D4298) stops calling it once the field == 3, so the 2->3
// transition occurs exactly once per entity. On that transition we ZERO the prev-mood
// field (this+0xDA8) and re-run UpdateMood: with prev==0 the whole m_Mood.currValue is
// seen as a fresh "add" delta, so every set mood bit is re-resolved against the now-loaded
// banks -> dword5CC populated -> A-pose fixed.
//
// CRITICAL FP/SSE SAFETY: this body is INTEGER-ONLY. No Log/sprintf/fopen/CRT, no float
// or double ops, no std::. It only: calls the trampoline, reads an int, compares, writes a
// zero, and makes one integer __thiscall. (Clobbering x87/SSE state inside the anim/weapon
// hot path is what broke ADS/firing with the earlier diagnostic hook.) Both functions are
// __thiscall on the SAME AI_EntityPlayer* (this in ecx); UpdateMood takes no stack args.
//
// Once-per-entity guard: a small static fixed-size array of already-handled `this`
// pointers. This touches NO game memory (cannot corrupt entity/anim state) and is purely
// integer compare/store. The 2->3 transition is already effectively single-shot (the caller
// stops invoking us at ==3); the array is belt-and-suspenders against any re-entry and
// caps total UpdateMood re-fires at one per distinct entity pointer.
void (__fastcall* org_UpdateAsyncLoadVisuals)(void*, void*);

#define MOODFIX_MAXENT 64
static void* g_moodfix_handled[MOODFIX_MAXENT] = { 0 };
static int   g_moodfix_count = 0;

void __fastcall UpdateAsyncLoadVisuals_moodfix(void* THIS, void* EDX)
{
	// Run the original per-frame async-load tick first (this performs the 1->2 / 2->3 writes).
	org_UpdateAsyncLoadVisuals(THIS, EDX);

	// Load-state field @ this+0x1400; 3 == fully loaded (banks ready). Act only on completion.
	if (*(DWORD*)((char*)THIS + 0x1400) != 3)
		return;

	// Once-per-entity guard (integer-only, no game memory touched).
	for (int i = 0; i < g_moodfix_count; i++)
		if (g_moodfix_handled[i] == THIS)
			return;
	if (g_moodfix_count < MOODFIX_MAXENT)
		g_moodfix_handled[g_moodfix_count++] = THIS;

	// Clear prev-mood (this+0xDA8) so UpdateMood treats the full current m_Mood as a fresh
	// add-delta and re-resolves every mood bit against the now-loaded anim banks.
	*(DWORD*)((char*)THIS + 0xDA8) = 0;

	// AI_EntityPlayer::UpdateMood @0x10076E90 -- __thiscall(this), no stack args.
	((void(__fastcall*)(void*, void*))(baseAddressAI + 0x76E90))(THIS, 0);
}

// ============================================================================
// LOCOMOTION-BANK DIAGNOSTIC  (installed by DetourDeployDiag, gated "_deploydiag_")
// ----------------------------------------------------------------------------
// Hard test of "are the body's locomotion anim banks actually bound?". cGestureMix::SwapBankId
// (@0x100EB070, from AI_EntityHuman_SetupGestureMixLocomotion at InitEntity) gates on
// AIDLL::ACT_bHasBankID(gao, bankID) for bank ids 100/110/120/130/140/150/160. If the body GAO
// lacks those banks at bind time, nothing binds -> rosace ACT layers 0-18 stay empty -> body
// A-pose + ACT_vGetRefBoxLinearVelocity == 0 (no root motion). (Arm-IK is separate SKE joint
// modifiers, so it keeps working -- exactly the dichotomy observed.) This hook reports, per
// locomotion bank, the gao and whether ACT_bHasBankID says it's present, logged ON CHANGE so a
// "0-at-bind-then-1-after-async-load" timing bug appears as two lines (0 then 1) and a permanent
// absence appears as a single 0. __cdecl(gao,bankID)->char. FP-safe: integer/hex log only,
// fxsave/fxrstor-bracketed; only the 7 locomotion bank ids are logged.
char (__cdecl* org_ACT_bHasBankID)(int, int) = 0;
__declspec(align(16)) static unsigned char g_bank_fxbuf[512];
static int g_bank_last[7] = { -2, -2, -2, -2, -2, -2, -2 };   // -2 = not yet observed

char __cdecl ACT_bHasBankID_diag(int gao, int bankID)
{
	char result = org_ACT_bHasBankID(gao, bankID);
	int idx = -1;
	switch (bankID)
	{
		case 100: idx = 0; break;
		case 110: idx = 1; break;
		case 120: idx = 2; break;
		case 130: idx = 3; break;
		case 140: idx = 4; break;
		case 150: idx = 5; break;
		case 160: idx = 6; break;
	}
	if (idx >= 0)
	{
		int r = (int)(unsigned char)result;
		if (r != g_bank_last[idx])
		{
			g_bank_last[idx] = r;
			_fxsave(g_bank_fxbuf);
			sprintf(buffer, "[BANK] bankID=%d gao=0x%08X hasBank=%d\n\0", bankID, gao, r);
			Log(buffer);
			_fxrstor(g_bank_fxbuf);
		}
	}
	return result;
}

// ============================================================================
// LOCOMOTION-APPLY WALK VALIDATION  (installed ONLY when a "_walktest_" file exists; else Patch1)
// ----------------------------------------------------------------------------
// sub_100ECC30 @0x100ECC30 (__thiscall, this == cGestureMix) is the PER-FRAME driver that plays the
// locomotion rosace clips onto the ACT anim layers (-> sub_100E8390 -> ACT_AnimLayer_PlayAction). Its
// first act is `a4 = this->dword5CC[12]`, which null-derefs when dword5CC (+0x5CC) == 0 (the anim
// init-ordering window before the rosace descriptor is populated). The OLD Patch1 "fixed" that crash by
// writing a blunt `ret` (0xC3) at the function start -- which disabled ALL locomotion clip playback ->
// permanent A-pose + zero root motion (THE entire walk bug).
//
// This guard instead runs the real function for EXACTLY ONE entity: the LOCAL player's pawn. playerAddress
// (captured by the AI_EntityPlayer::UpdateWarning hook) IS the local controllable pawn; its cGestureMix is
// *(playerAddress+0xF6C)+8 (manager @pawn+0xF6C, cGestureMix @manager+8, dword5CC @cGestureMix+0x5CC -- all
// cross-checked vs the [VEL] diag's descriptor address *(pawn+0xF6C)+0x5D4 == cGestureMix+0x5CC, and vs
// sub_100ECC30 reading dword5CC at its this+0x5CC). playerAddress stays 0 until the local pawn is live in a
// match, so AT THE LOBBY/CHAR-SELECT this matches nothing -> every call is skipped (Patch1-equivalent) ->
// it CANNOT crash login the way the old per-frame eStateID gate did (that gate evaluated a transiently-wrong
// state during the lobby transition). _moodfix_ must be armed too: it populates dword5CC at visual-load-
// complete (runtime descSeen=1 confirmed). Integer-only: no FP/CRT, so the original keeps its x87/SSE anim
// state intact (no Heisenbug). This is a VALIDATION harness -- if the body walks, the root cause is proven
// and the real fix moves server-side (spawn ordering), retiring both this and Patch1.
char* (__fastcall* org_LocomotionApply)(void*, void*) = 0;

char* __fastcall LocomotionApply_guard(void* THIS, void* EDX)
{
	if (THIS == 0 || *(DWORD*)((char*)THIS + 0x5CC) == 0)
		return 0;                                   // null dword5CC -> the crash case Patch1 guarded
	if (playerAddress == 0)
		return 0;                                   // local pawn not live yet (lobby/menus) -> skip ALL (lobby-safe)
	DWORD mgr = *(DWORD*)(playerAddress + 0xF6C);   // local pawn's gesture-mix manager
	if (mgr == 0 || (void*)(mgr + 8) != THIS)
		return 0;                                   // not the LOCAL player's cGestureMix (bots/dolls/menu) -> skip
	return org_LocomotionApply(THIS, EDX);          // local player + descriptor set -> play locomotion clips (walk)
}

// ============================================================================
// DEPLOY-RAMP DIAGNOSTIC  (installed only when a "_deploydiag_" file exists)
// ----------------------------------------------------------------------------
// Goal (ground-truth WHY the local pawn never gets input control): the deploy /
// combat-input ramp in AI_EntityPlayerAbstract::Spawn (0x100D8C20) never reaches
// threshold, so cGameStatsStore::SetAsSpawned + AI_NetworkManager::SetStateAndEndIfClient(4)
// never fire and the player can't move/ADS/fire.
//
// RE of the ramp (imagebase 0x10000000, RVAs off baseAddressAI):
//   v5 = bIsInLoopAdversarialState()   [cNetRulesManager.Instance+0xA8 == 4]
//        && !bIsInWarmupState()        [matchSrv = NetRules+0x30C; matchSrv ? matchSrv+0x54==1 : 0]
//        && (currentState[+0x15C]==2 || bIsClientReady())   [params bitfield14 & 0x2000]
//   while v5: IncInputValue (0x100D58B0) ramps accumulator @this+0x1D0 by the frame
//   delta -- BUT if AIDLL::INP_bInputHasFocus() (0x100D4A60) returns true it FORCES the
//   accumulator to 0.0 every frame (fldz), so the ramp can never complete.
//   SetAsSpawned fires when accumulator >= [GlobalGameplay::Get()->globals(+0x58) + 0x6C]
//   (the deploy delay; written by the Zen IdleActivation script, can be garbage/NaN if
//   that data bank didn't load) + float1CC(+0x1CC).
//
// This hook captures, ON CHANGE ONLY, for the local pawn:
//   inpFocus  = INP_bInputHasFocus()        (suspect (b): ramp perpetually reset)
//   clientRdy = bIsClientReady()            (suspect (a): 0x2000 not landing)
//   v5        = recomputed ramp gate
//   state     = currentState (+0x15C)
//   accumBits = RAW 32-bit bits of accumulator (+0x1D0)   -- NOT loaded as float
//   delayBits = RAW 32-bit bits of deploy delay [globals+0x6C] (suspect (c): NaN/huge)
//   f1CCBits  = RAW 32-bit bits of float1CC (+0x1CC)
//   reached   = integer proxy for "SetAsSpawned branch reached": for two NON-negative,
//               non-NaN IEEE-754 floats, (accumBits >= delayBits) as unsigned ints is
//               equivalent to (accum >= delay). Flagged INVALID if either looks like a
//               NaN/Inf (exp all-ones) or has the sign bit set, so a bogus delay is obvious.
//
// CRITICAL FP/SSE SAFETY (this is what broke ADS/firing before):
//   * The real Spawn trampoline runs FIRST and does all its own x87 work.
//   * Everything the hook then does is bracketed by _fxsave/_fxrstor on a 16-byte-aligned
//     buffer, so the FPU+SSE register file is byte-for-byte identical when the hook returns
//     into the game. Inside that bracket the hook still only does INTEGER work plus two
//     integer-returning game calls (bIsClientReady, INP_bInputHasFocus) and, rarely (on
//     change), sprintf/Log. Floats are only ever read as raw DWORD bits -- never loaded
//     into x87/SSE as floating point by our code.
//   * No per-frame logging: a static snapshot of the tracked integers gates emission.
// I/O note: bIsClientReady() itself emits one DBG_SendSessionLog line on the frames where
// the networked player-params aren't available yet; that is the game's own log, fires at
// most a handful of times before the server sets the bit, and is harmless.
typedef int (__fastcall* SPAWN_FN)(void*, void*, float);
SPAWN_FN org_Spawn_deploydiag = 0;

// 16-byte-aligned FXSAVE area (512 bytes). __declspec(align) keeps it off the (possibly
// misaligned) stack so fxsave/fxrstor never fault.
__declspec(align(16)) static unsigned char g_dd_fxbuf[512];

// previous-value tracker (all integers); -1/0 sentinels so the first observation prints.
static int  g_dd_init      = 0;
static int  g_dd_pInpFocus = -1;
static int  g_dd_pClientRdy= -1;
static int  g_dd_pV5       = -1;
static int  g_dd_pState    = -1;
static DWORD g_dd_pAccum   = 0xFFFFFFFF;
static DWORD g_dd_pDelay   = 0xFFFFFFFF;
static DWORD g_dd_pF1CC    = 0xFFFFFFFF;
static int  g_dd_pReached  = -1;
static int  g_dd_pInSeen   = -1;   // [INPUT] diag: 1 once the controllable pawn's input axes/magnitude are non-zero
static int  g_dd_pMoveMode = -1;    // [VEL] diag: pawn move-mode byte (+0xDA0); 0 == velocity force-zeroed in Master
static int  g_dd_pVelSeen  = -1;    // [VEL] diag: 1 once pawn velocity (+0x700) is non-zero
static int  g_dd_pRbSeen   = -1;    // [VEL] diag: 1 once refBoxLinearVelocity (+0x6F4, walk root motion) is non-zero
static int  g_dd_pDescSeen = -1;    // [VEL] diag: 1 once the locomotion rosace descriptor (*(pawn+0xF6C)+0x5D4) is non-null
static int  g_dd_pLoadState= -1;    // [VEL] diag: pawn visual-load state (+0x1400); 3 == banks fully loaded (moodfix trigger)
static int  g_dd_pRsSeen   = -1;    // [VEL] diag: 1 once PCSet_AnimationRosaceSpeed (+0x718, input->walk-blend speed) is non-zero
// [FIRE] diag (gated by a "_firediag_" file; g_fireDiagOn set in DetourDeployDiag): WHY can't the player shoot?
static int  g_fireDiagOn   = 0;     // 1 if "_firediag_" exists -> enable the [FIRE] read below (independent kill switch)
static int  g_dd_pWpnNull  = -2;    // [FIRE] diag: 1 if GetWeaponComponent(pawn)==null (no weapon component -> can't fire)
static int  g_dd_pRounds   = -2;    // [FIRE] diag: clip rounds = *(*(wpn+0x4EC)+8); 0 == ammo gate (bIsReadyToFire/bCanFire) fails -> can't fire
static int  g_dd_pReady    = -1;    // [RDY] diag: bIsReadyToFire(pawn) -- 1=weapon reports ready (no-fire is upstream: trigger/intent), 0=a weapon readiness check fails. SAFE (no input-device access).
static int  g_dd_pFirePresent = -2;  // [INTENT] diag: 1 if cActionFire is ordered (action-5 engaged at all)
static int  g_dd_pFireCur     = -2;  // [INTENT] diag: actionFire[+0x40] this-frame fire intent (1=TryFire set it)
static int  g_dd_pFirePrev    = -2;  // [INTENT] diag: actionFire[+0x3F] prev-frame fire intent
static DWORD g_dd_pPadCtx = 0xFFFFFFFF;  // [TRIG] diag: player input-device context ptr = *(pawn+0x3D0)
static int  g_dd_pPadCh   = -2;  // [TRIG] diag: pPadCtx->byte4 input channel (0xFF/255 = unassigned -> fire poll short-circuits to 0)
static int  g_dd_pPadDis  = -2;  // [TRIG] diag: pPadCtx->byte13 disabled flag (!=0 -> fire poll returns 0)
static int  g_dd_pGbusy   = -2;  // [GATE] diag: v14 reload/busy field pawn+0x3B8 (!=0 -> v13=0 -> no fire)
static int  g_dd_pGb580   = -2;  // [GATE] diag: v14 busy predicate entity vtable+580 (1 -> v13=0 -> no fire)
static int  g_dd_pGc572   = -2;  // [GATE] diag: cover gate entity vtable+572 (1 -> discharge skipped)
static int  g_dd_pGc576   = -2;  // [GATE] diag: cover gate entity vtable+576 (1 -> discharge skipped)
static int  g_dd_pGrate   = -2;  // [GATE] diag: rate/ready entity vtable+236 (0 -> discharge skipped)
static int  g_dd_pGfm468  = -2;  // [GATE] diag: fire-mode wpn+0x468 (v12 blocks iff !=0 AND fm464==3)
static int  g_dd_pGfm464  = -2;  // [GATE] diag: fire-mode wpn+0x464 fireModeCompId
static int  g_dd_pSerFlags = -2;  // [SHOT] diag: serializationFlags byte pawn+0x30 (bit0 = local-player; gates SetShootingReplData's body)
static int  g_dd_pShotEver = 0;   // [SHOT] diag: sticky -- 1 once pawn+0x274 (post-vtable+228 'shot fired' flag) is ever seen set (discharge reached)
static int  g_dd_pShotLogd = -2;  // [SHOT] diag: last-logged g_dd_pShotEver
static int  g_dd_pOHandle = -2;  // [OWN] diag: weapon ownerEntityHandle (weapon+0x24); ctor zeroes it, set to the pawn during equip/build
static int  g_dd_pOGot    = -2;  // [OWN] diag: GetOwnerEntityPawn(weapon)!=0 (bCanFire's hard owner gate; 0 = no owner -> no shot)
// [ADS] diag (runs whenever _deploydiag_ is installed): WHY is the aim-down-sights camera at the wrong
// position? The camera EYE is stance-mode-TABLE-driven (eye offset = CameraSettings + 416*mode, where
// mode = camera m_ShootPosition.currValue.x); mode 0 == "looking at the legs" and it NEVER reads the
// weapon iron bone. AI_Camera_SelectStanceCameraMode only returns an aim mode when the "aiming" mood bit
// (m_Mood.currValue & 0x40000) is set AND the OTS->iron state (dword480) is engaged. These 3 pawn fields split it.
static int  g_ads_init    = 0;   // [ADS] emit-on-change init
static int  g_ads_pD480   = -2;  // [ADS] dword480 OTS/iron state (+0x480): 2=iron/ADS, 0=hip
static int  g_ads_pAimBit = -2;  // [ADS] m_Mood.currValue & 0x40000 "aiming" bit (+0x824)
static int  g_ads_pStance = -2;  // [ADS] currentStanceID (+0x474)

// True if the raw IEEE-754 bits are a usable, comparable non-negative finite number
// (sign clear, exponent not all-ones). Integer-only; does not load the value as a float.
static int dd_isUsableNonNeg(DWORD bits)
{
	if (bits & 0x80000000u) return 0;            // negative
	if ((bits & 0x7F800000u) == 0x7F800000u) return 0; // Inf or NaN
	return 1;
}

int __fastcall Spawn_deploydiag(void* THIS, void* EDX, float dt)
{
	// 1) Run the real per-frame spawn tick first (full original behaviour + its own FP).
	int result = org_Spawn_deploydiag(THIS, EDX, dt);

	// 2) Only the local pawn runs the ramp (serializationFlags @ +0x30 & 1), matching the
	//    original's own gate. Integer read; bail before touching the FPU otherwise.
	if (THIS == 0 || (*(unsigned char*)((char*)THIS + 0x30) & 1) == 0)
		return result;

	// 3) Save the game's post-Spawn FPU+SSE state; restore on every return path below so
	//    the register file we hand back is identical. Our diagnostic lives entirely inside.
	_fxsave(g_dd_fxbuf);

	char* p = (char*)THIS;

	// --- gather (integer only) ---------------------------------------------------------
	int state = *(int*)(p + 0x15C);

	// loopAdv / warmup via direct memory reads (avoids the singleton Get() accessors, which
	// __debugbreak if the instance is null). Mirror the exact field tests from the disasm.
	int loopAdv = 0, warmup = 0;
	DWORD netRules = *(DWORD*)(baseAddressAI + 0x6CC310);   // cNetRulesManager::Instance
	if (netRules)
	{
		loopAdv = (*(int*)(netRules + 0xA8) == 4) ? 1 : 0;
		DWORD matchSrv = *(DWORD*)(netRules + 0x30C);
		warmup = (matchSrv && *(int*)(matchSrv + 0x54) == 1) ? 1 : 0;
	}

	// bIsClientReady(): __thiscall(this) -> al. Integer-only body (verified).
	int clientRdy = ((char(__fastcall*)(void*, void*))(baseAddressAI + 0xD5A30))(THIS, 0) ? 1 : 0;

	// AIDLL::INP_bInputHasFocus(): __cdecl -> al. No FP in its body (engine vtable bool).
	int inpFocus = ((char(__cdecl*)(void))(baseAddressAI + 0xD4A60))() ? 1 : 0;

	// recompute v5 exactly as Spawn does.
	int v5 = (loopAdv && !warmup && (state == 2 || clientRdy)) ? 1 : 0;

	// raw float bits (NEVER loaded as float by us).
	DWORD accumBits = *(DWORD*)(p + 0x1D0);
	DWORD f1CCBits  = *(DWORD*)(p + 0x1CC);

	// deploy delay = [GlobalGameplay::Get()->globals(+0x58) + 0x6C], as RAW bits.
	// GlobalGameplay::Get() (0x1020AF60) constructs the singleton on first use; it is
	// integer-returning. Guard the chained pointer derefs.
	DWORD delayBits = 0xFFFFFFFF;
	DWORD ggGlobals = 0;
	{
		void* gg = ((void*(__cdecl*)(void))(baseAddressAI + 0x20AF60))();
		if (gg)
		{
			ggGlobals = *(DWORD*)((char*)gg + 0x58);
			if (ggGlobals)
				delayBits = *(DWORD*)(ggGlobals + 0x6C);
		}
	}

	// integer proxy for "SetAsSpawned branch reached": valid only for two usable
	// non-negative finite floats. -1 == can't tell (delay/accum is NaN/Inf/negative).
	// DEPLOY-FAST FIX: globals+0x6C ("IdleActivation") is the deploy threshold, stock 120.0s -- the idle
	// auto-deploy fallback. The emulator never shows the deploy menu, so the player falls through to that
	// 120s timer before the ramp grants control. Rewrite the known stock 120.0 to 0.5s for the local pawn
	// so the deploy completes in <1s. Integer write (no FP), inside the fxsave/fxrstor bracket.
	/* deploy-fast REVERTED: completing this ramp calls SetStateAndEndIfClient(4) = LaunchEndGameClient = ENDS THE MATCH (the kick) -- the ramp @0x100d8c20 is the idle match-end timer, NOT the deploy/control gate. Diag below stays. */

	int reached;
	if (dd_isUsableNonNeg(accumBits) && dd_isUsableNonNeg(delayBits))
		reached = (accumBits >= delayBits) ? 1 : 0;
	else
		reached = -1;

	// --- emit ONLY on change ------------------------------------------------------------
	if (!g_dd_init
		|| inpFocus  != g_dd_pInpFocus
		|| clientRdy != g_dd_pClientRdy
		|| v5        != g_dd_pV5
		|| state     != g_dd_pState
		|| accumBits != g_dd_pAccum
		|| delayBits != g_dd_pDelay
		|| f1CCBits  != g_dd_pF1CC
		|| reached   != g_dd_pReached)
	{
		g_dd_init       = 1;
		g_dd_pInpFocus  = inpFocus;
		g_dd_pClientRdy = clientRdy;
		g_dd_pV5        = v5;
		g_dd_pState     = state;
		g_dd_pAccum     = accumBits;
		g_dd_pDelay     = delayBits;
		g_dd_pF1CC      = f1CCBits;
		g_dd_pReached   = reached;

		// sprintf/Log use only integers and raw hex bits -- no %f, no float math.
		sprintf(buffer,
			"[DEPLOY] this=0x%08X state=%d v5=%d inpFocus=%d clientRdy=%d "
			"loopAdv=%d warmup=%d accum=0x%08X delay=0x%08X f1CC=0x%08X reached=%d "
			"ggGlobals=0x%08X\n\0",
			(DWORD)THIS, state, v5, inpFocus, clientRdy,
			loopAdv, warmup, accumBits, delayBits, f1CCBits, reached,
			ggGlobals);
		Log(buffer);
	}

	// --- INPUT diagnostic: is the controllable PAWN actually receiving input? ------------
	// abstract->masterEntityPlayer (+0x18C) = the pawn (AI_EntityPlayer). The PC input poll
	// (sub_100A7D20) writes the pawn's input axes at pawn+0x3EC/0x3F0 and the movement
	// magnitude at pawn+0x464 (consumed by cActionSelectorPC::TryWalk). pad = *(pawn+0x3D0)
	// is the bound pad GetPadInput reads. RAW integer bits only -- no float math. Emits when
	// input is first seen / lost: if you HOLD W/A/S/D and no line with inSeen=1 ever appears,
	// input is not reaching the pawn (focus / pad-binding / action-dispatch).
	{
		// Only at state 5 (Loop) is masterEntityPlayer (+0x18C) reliably the fully-built pawn;
		// earlier (join/spawn) +0x18C may be 0 or uninitialized garbage, so dereferencing
		// pawn+0x3EC then access-violates -> the crash-on-join. Gate on state==5 AND range-check
		// the pawn pointer (plausible aligned user-mode address) before touching any pawn field.
		DWORD pawn = (state == 5) ? *(DWORD*)(p + 0x18C) : 0;
		DWORD inX = 0, inY = 0, inMag = 0, padPtr = 0;
		if (pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
		{
			inX    = *(DWORD*)(pawn + 0x3EC);
			inY    = *(DWORD*)(pawn + 0x3F0);
			inMag  = *(DWORD*)(pawn + 0x464);
			padPtr = *(DWORD*)(pawn + 0x3D0);

			// VELOCITY-CHAIN DIAGNOSTIC (read-only; mood theory disproven -- m_Mood already = 3).
			// Input reaches the pawn (inMag=1.0) but it doesn't move. AI_EntityHuman::Master
			// @0x10086a60 sets velocity(+0x700) from refBoxLinearVelocity(+0x6F4 = the walk anim's
			// ROOT MOTION), then a move-mode switch on byte +0xDA0 where case 0 FORCE-ZEROES
			// velocity. So either move-mode==0 zeroes it, or refBox stays 0 (walk anim not
			// playing). Log move-mode + whether velocity / root-motion ever go non-zero, on change.
			DWORD velX = *(DWORD*)(pawn + 0x700);
			DWORD rbX  = *(DWORD*)(pawn + 0x6F4);
			DWORD rbY  = *(DWORD*)(pawn + 0x6F8);
			int moveMode = *(unsigned char*)(pawn + 0xDA0);
			// Rosace descriptor (subagent's root cause): cGestureMix.dword5CC = *(pawn+0xF6C)+0x5D4.
			// NULL == no locomotion rosace installed == zero root motion. loadState (pawn+0x1400)==3
			// means the anim banks finished loading -- exactly when the armed _moodfix_ hook re-fires
			// UpdateMood. So if loadState reaches 3 but desc stays 0, the moodfix's UpdateMood is NOT
			// installing the rosace (mood-bit/rosace mismatch or the hook isn't attached). Read-only.
			int loadState = *(int*)(pawn + 0x1400);
			DWORD gMgr = *(DWORD*)(pawn + 0xF6C);
			DWORD desc = 0;
			if (gMgr >= 0x00010000 && gMgr < 0x7FFF0000 && (gMgr & 3) == 0)
				desc = *(DWORD*)(gMgr + 0x5D4);
			// PCSet_AnimationRosaceSpeed (+0x718): the speed the input cursor is supposed to feed the
			// walk rosace (set by UpdateAnimationRosaceSpeed from ProcessKeyboardInput). If this stays 0
			// while a key is held (cursor mag +0x464 == 1.0), the input->locomotion link is GATED (e.g.
			// player not in a fully-deployed/playable state) -- supports the "incomplete deploy" theory.
			DWORD rsSpeed = *(DWORD*)(pawn + 0x718);
			int velSeen  = (velX != 0) ? 1 : 0;
			int rbSeen   = (rbX || rbY) ? 1 : 0;
			int descSeen = (desc != 0) ? 1 : 0;
			int rsSeen   = (rsSpeed != 0) ? 1 : 0;
			if (moveMode != g_dd_pMoveMode || velSeen != g_dd_pVelSeen || rbSeen != g_dd_pRbSeen
				|| descSeen != g_dd_pDescSeen || loadState != g_dd_pLoadState || rsSeen != g_dd_pRsSeen)
			{
				g_dd_pMoveMode  = moveMode;
				g_dd_pVelSeen   = velSeen;
				g_dd_pRbSeen    = rbSeen;
				g_dd_pDescSeen  = descSeen;
				g_dd_pLoadState = loadState;
				g_dd_pRsSeen    = rsSeen;
				sprintf(buffer,
					"[VEL] loadState=%d moveMode=%d descSeen=%d rsSeen=%d rsSpeed=0x%08X rbSeen=%d velSeen=%d desc=0x%08X rbX=0x%08X\n\0",
					loadState, moveMode, descSeen, rsSeen, rsSpeed, rbSeen, velSeen, desc, rbX);
				Log(buffer);
			}
			// [ADS] read the 3 pawn fields that drive the stance-camera mode (see [ADS] tracker comment
			// above). Raw integer reads of the validated pawn -- crash-safe, same style as [VEL]/[FIRE].
			{
				int adsD480   = *(int*)(pawn + 0x480);     // OTS<->IronSight state: 0=hip, 2=iron/ADS
				DWORD adsMood = *(DWORD*)(pawn + 0x824);   // m_Mood.currValue
				int adsStance = *(int*)(pawn + 0x474);     // currentStanceID
				int adsAimBit = (adsMood & 0x40000) ? 1 : 0;
				if (!g_ads_init || adsD480 != g_ads_pD480 || adsAimBit != g_ads_pAimBit || adsStance != g_ads_pStance)
				{
					g_ads_init = 1; g_ads_pD480 = adsD480; g_ads_pAimBit = adsAimBit; g_ads_pStance = adsStance;
					sprintf(buffer, "[ADS] d480=%d (2=iron) mood=0x%08X aimBit=%d (0x40000) stance=%d\n\0",
						adsD480, adsMood, adsAimBit, adsStance);
					Log(buffer);
				}
			}
		}
		// --- [FIRE] diagnostic: WHY can't the player shoot? Decisive gate = clip rounds (ammo). ----
			// Gated by "_firediag_" (g_fireDiagOn). bIsReadyToFire @0x10076d00 AND bCanFire @0x10068890 both
			// abort unless rounds = *(*(GetWeaponComponent(pawn)+0x4EC)+8) > 0 (clip rounds set by InitAmmoCounter
			// from weapon property 43 = clip size). Provisioning audit: the equipped weapon (mapKey 170) is a
			// degenerate DB row whose component modifier lists carry NO clip-size property -> clipSize 0 ->
			// rounds 0 -> no shot. This settles it:  wpnNull=1 -> weapon component not built;  rounds=0 -> ammo
			// gate is the blocker (fix weapon DB/loadout);  rounds>0 -> block is elsewhere (state/ADS/jump).
			// GetWeaponComponent @0x1008bbf0 (__thiscall(pawn)) is a side-effect-free getter; called inside the
			// existing fxsave bracket so any FP it touches is restored. pawn re-validated for safety.
			if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
			{
				DWORD fwpn = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)pawn, 0);
				DWORD fammo = 0; int frounds = -1;
				if (fwpn >= 0x00010000 && fwpn < 0x7FFF0000 && (fwpn & 3) == 0)
				{
					fammo = *(DWORD*)(fwpn + 0x4EC);
					if (fammo >= 0x00010000 && fammo < 0x7FFF0000 && (fammo & 3) == 0)
						frounds = (int)*(DWORD*)(fammo + 8);
				}
				int fwpnNull = (fwpn == 0) ? 1 : 0;
				if (fwpnNull != g_dd_pWpnNull || frounds != g_dd_pRounds)
				{
					g_dd_pWpnNull = fwpnNull;
					g_dd_pRounds  = frounds;
					sprintf(buffer, "[FIRE] wpnNull=%d rounds=%d wpn=0x%08X ammoCtr=0x%08X\n\0", fwpnNull, frounds, fwpn, fammo);
					Log(buffer);
				}
			}
			// [RDY] weapon-side fire-readiness -- SAFE (no input-device access; the old [ACT] pad probe crashed at spawn).
				// bIsReadyToFire(pawn) @0x10076d00: 1=weapon reports ready (a no-fire is then upstream); 0=a weapon readiness check fails.
				if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
				{
					int rdy = ((char(__fastcall*)(void*, void*))(baseAddressAI + 0x76D00))((void*)pawn, 0) ? 1 : 0;
					if (rdy != g_dd_pReady)
					{
						g_dd_pReady = rdy;
						sprintf(buffer, "[RDY] bIsReadyToFire=%d\n\0", rdy);
						Log(buffer);
					}
				}
				// [INTENT] fire-intent flag: actionFire[+0x40] (cur) / [+0x3F] (prev) via pawn+0x2C8 -> GetAction::cActionFire (RVA 0x9BFF0).
				// present=0: action-5 never ordered (trigger/binding/pad side). cur/prev stuck 0 while clicking: a TryFire predicate/poll fails.
				// cur or prev ever 1 while clicking: intent IS set -> blocker is downstream in the executor (vtable[228] discharge). Field reads only; crash-safe.
				if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
				{
					DWORD aFire = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x9BFF0))((void*)(pawn + 0x2C8), 0);
					int present = (aFire != 0) ? 1 : 0;
					int icur = -1, iprev = -1;
					if (aFire >= 0x00010000 && aFire < 0x7FFF0000 && (aFire & 3) == 0)
					{
						icur  = *(unsigned char*)(aFire + 0x40);
						iprev = *(unsigned char*)(aFire + 0x3F);
					}
					if (present != g_dd_pFirePresent || icur != g_dd_pFireCur || iprev != g_dd_pFirePrev)
					{
						g_dd_pFirePresent = present;
						g_dd_pFireCur = icur;
						g_dd_pFirePrev = iprev;
						sprintf(buffer, "[INTENT] aFire=0x%08X present=%d cur=%d prev=%d\n\0", aFire, present, icur, iprev);
						Log(buffer);
					}
				}
				// [TRIG] fire-trigger input-context gate -- SAFE field reads only (no device call). pPadCtx = *(pawn+0x3D0).
				// IsKeyPressed(pPadCtx,5) (the action-5/fire poll) returns 0 BEFORE reading the device if channel(byte4)==0xFF (no device assigned) or disabled(byte13)!=0.
				if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
				{
					DWORD pPadCtx = *(DWORD*)(pawn + 0x3D0);
					int ch = -1, dis = -1;
					if (pPadCtx >= 0x00010000 && pPadCtx < 0x7FFF0000 && (pPadCtx & 3) == 0)
					{
						ch  = *(unsigned char*)(pPadCtx + 4);
						dis = *(unsigned char*)(pPadCtx + 0xD);
					}
					if (pPadCtx != g_dd_pPadCtx || ch != g_dd_pPadCh || dis != g_dd_pPadDis)
					{
						g_dd_pPadCtx = pPadCtx;
						g_dd_pPadCh = ch;
						g_dd_pPadDis = dis;
						sprintf(buffer, "[TRIG] pPadCtx=0x%08X channel=%d (0xFF/255=unassigned) disabled=%d\n\0", pPadCtx, ch, dis);
						Log(buffer);
					}
				}
				// [GATE] cActionFire::UpdateInternal discharge gates -- WHY no fire despite intent set. entity==pawn (GetWeaponComponent is called on pawn).
				// discharge (weapon vtable+228) needs ALL: !v14(busy: pawn+0x3B8 || pawn.vtable+580) && !cover(pawn.vtable+572/+576) && rate(pawn.vtable+236) && v12(NOT(wpn+0x468!=0 && wpn+0x464==3)).
				// vtable predicates called exactly like the [RDY] bIsReadyToFire read; pawn validated; inside the fxsave bracket.
				if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
				{
					DWORD gev = *(DWORD*)pawn;
					DWORD gwpn = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)pawn, 0);
					int gbusy238 = (*(DWORD*)(pawn + 0x3B8)) ? 1 : 0;
					int gb580 = -1, gc572 = -1, gc576 = -1, grate = -1;
					if (gev >= 0x00010000 && gev < 0x7FFF0000 && (gev & 3) == 0)
					{
						gb580 = ((char(__fastcall*)(void*, void*))(*(DWORD*)(gev + 580)))((void*)pawn, 0) ? 1 : 0;
						gc572 = ((char(__fastcall*)(void*, void*))(*(DWORD*)(gev + 572)))((void*)pawn, 0) ? 1 : 0;
						gc576 = ((char(__fastcall*)(void*, void*))(*(DWORD*)(gev + 576)))((void*)pawn, 0) ? 1 : 0;
						grate = ((char(__fastcall*)(void*, void*))(*(DWORD*)(gev + 236)))((void*)pawn, 0) ? 1 : 0;
					}
					int gfm468 = -1, gfm464 = -1;
					if (gwpn >= 0x00010000 && gwpn < 0x7FFF0000 && (gwpn & 3) == 0)
					{
						gfm468 = *(unsigned char*)(gwpn + 0x468);
						gfm464 = (int)*(DWORD*)(gwpn + 0x464);
					}
					if (gbusy238 != g_dd_pGbusy || gb580 != g_dd_pGb580 || gc572 != g_dd_pGc572 || gc576 != g_dd_pGc576 || grate != g_dd_pGrate || gfm468 != g_dd_pGfm468 || gfm464 != g_dd_pGfm464)
					{
						g_dd_pGbusy = gbusy238; g_dd_pGb580 = gb580; g_dd_pGc572 = gc572; g_dd_pGc576 = gc576; g_dd_pGrate = grate; g_dd_pGfm468 = gfm468; g_dd_pGfm464 = gfm464;
						sprintf(buffer, "[GATE] busy238=%d busy580=%d cover572=%d cover576=%d rate236=%d fm468=%d fm464=%d\n\0", gbusy238, gb580, gc572, gc576, grate, gfm468, gfm464);
						Log(buffer);
					}
				}
				// [SHOT] confirm the discharge actually executes + read the SetShootingReplData gate directly.
				// serializationFlags=pawn+0x30 (bit0 = local-player; gates the fire-replication body). pawn+0x274 is set to 1
				// right after the vtable+228 (SetShootingReplData) discharge -> sticky 'dischargeEverReached' = is the fire call even reached.
				if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
				{
					int serFlags = (int)(*(unsigned char*)(pawn + 0x30));
					if (*(unsigned char*)(pawn + 0x274)) g_dd_pShotEver = 1;
					if (serFlags != g_dd_pSerFlags || g_dd_pShotEver != g_dd_pShotLogd)
					{
						g_dd_pSerFlags = serFlags; g_dd_pShotLogd = g_dd_pShotEver;
						sprintf(buffer, "[SHOT] serFlags=0x%02X bit0=%d dischargeEverReached=%d\n\0", serFlags, serFlags & 1, g_dd_pShotEver);
						Log(buffer);
					}
				}
				// [OWN] weapon owner gate -- bCanFire(1) HARD-requires GetOwnerEntityPawn(weapon) != 0. weapon+0x24 = ownerEntityHandle (ctor zeroes it; set to the pawn during equip/build).
				// GetOwnerEntityPawn @0x10065FE0 is read-only (resolves the handle via cEntityManager + requires a pawn subclass). getOwnerPawn=0 => bCanFire aborts EVERY shot.
				if (g_fireDiagOn && pawn >= 0x00010000 && pawn < 0x7FFF0000 && (pawn & 3) == 0)
				{
					DWORD owpn = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)pawn, 0);
					int ohandle = 0, ogot = -1;
					if (owpn >= 0x00010000 && owpn < 0x7FFF0000 && (owpn & 3) == 0)
					{
						ohandle = (int)*(DWORD*)(owpn + 0x24);
						ogot = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x65FE0))((void*)owpn, 0) ? 1 : 0;
					}
					if (ohandle != g_dd_pOHandle || ogot != g_dd_pOGot)
					{
						g_dd_pOHandle = ohandle; g_dd_pOGot = ogot;
						sprintf(buffer, "[OWN] ownerHandle=0x%08X getOwnerPawn=%d (bCanFire needs getOwnerPawn=1)\n\0", ohandle, ogot);
						Log(buffer);
					}
				}
				int inSeen = (inX || inY || inMag) ? 1 : 0;
		if (inSeen != g_dd_pInSeen)
		{
			g_dd_pInSeen = inSeen;
			sprintf(buffer,
				"[INPUT] pawn=0x%08X inSeen=%d inX=0x%08X inY=0x%08X inMag=0x%08X pad=0x%08X inpFocus=%d\n\0",
				pawn, inSeen, inX, inY, inMag, padPtr, inpFocus);
			Log(buffer);
		}
	}

	// 4) Restore the saved FPU+SSE state -> byte-identical to post-trampoline.
	_fxrstor(g_dd_fxbuf);
	return result;
}

// ============================================================================
// WEAPON-CUSTOMIZE STORE-FUNCTOR DIAGNOSTIC  (installed only when a "_customizediag_" file exists)
// ----------------------------------------------------------------------------
// Goal: ground-truth WHY the weapon-customize page offers no buyable attachments.
// AI_WeaponCustomizeHelper::BuildAvailableComponent3DObjects builds each slot's option list via:
//   GetFunctorsFromList(storeFunctorList, &{u16 itemType=4, u16 weaponType})  -> the candidate SKUs,
//   then AI_WeaponCustomizeHelper::GetAttachCompType(component) per SKU (compat gate; -1 == rejected).
// Two read-only hooks split the failure cleanly:
//   0x121B80 GetFunctorsFromList(this=ecx, int* functorId)  -> for component lookups (functorId[0]==4)
//            log the weaponType key + whether a functor bucket was FOUND + its SKU count. MISSING/0 ==
//            the {4,weaponType} functor was never populated (functor-build / bridge-key mismatch).
//   0x121D10 AI_WeaponCustomizeHelper::GetAttachCompType(this=ecx, int compMapKey) -> log (comp,result).
//            result == -1 means the served component was REJECTED by the weapon compat check.
// FP-safe: the real function runs first; the (rare, menu-time) logging is bracketed by _fxsave/_fxrstor
// on a 16-byte-aligned buffer; integer/hex only (no float math). functorId is a pointer passed as int.
// ============================================================================
DWORD      (__fastcall* org_GetFunctorsFromList_diag)(void*, void*, int) = 0;
signed int (__fastcall* org_GetAttachCompType_diag)(void*, void*, int) = 0;
__declspec(align(16)) static unsigned char g_cz_fxbuf[512];

DWORD __fastcall GetFunctorsFromList_diag(void* THIS, void* EDX, int functorId)
{
	DWORD result = org_GetFunctorsFromList_diag(THIS, EDX, functorId);
	// Only the component-functor lookups ({itemType==4, weaponType}) are interesting.
	if (functorId && *(unsigned short*)functorId == 4)
	{
		_fxsave(g_cz_fxbuf);
		unsigned short weaponType = *(unsigned short*)(functorId + 2);
		// GetFunctorsFromList returns the BagTypeVector {dword0, ptrEntries@+4, size@+8}; size == SKU count.
		int count = result ? *(int*)(result + 8) : 0;
		sprintf(buffer, "[CZ] GetFunctorsFromList {4,%u} -> %s skuCount=%d\n\0",
			weaponType, result ? "FOUND" : "MISSING", count);
		Log(buffer);
		_fxrstor(g_cz_fxbuf);
	}
	return result;
}

signed int __fastcall GetAttachCompType_diag(void* THIS, void* EDX, int compMapKey)
{
	signed int result = org_GetAttachCompType_diag(THIS, EDX, compMapKey);
	_fxsave(g_cz_fxbuf);
	sprintf(buffer, "[CZ] GetAttachCompType comp=%d -> %d%s\n\0",
		compMapKey, result, (result == -1) ? "  (REJECTED)" : "");
	Log(buffer);
	_fxrstor(g_cz_fxbuf);
	return result;
}

// [CZT] WHY the customize opens in template (read-only) mode. AI_StorePage/AI_AdWidget are the ONLY
// callers of SetUseTemplate(1); the editable editor is non-template (bUseTemplate inits 0, Reset clears
// it). These two hooks reveal (a) who/when forces template, and (b) the bUseTemplate state when the slots
// are drawn. bUseTemplate @ AI_WeaponCustomizeHelper+0xAC (verified: SetUseTemplate `mov [ecx+0ACh], al`).
// FP-safe; _ReturnAddress() captures the game call site -> mapped to the AICLASS RVA.
char (__fastcall* org_SetUseTemplate_diag)(void*, void*, char) = 0;
bool (__fastcall* org_IsDetachable_diag)(void*, void*, int) = 0;

char __fastcall SetUseTemplate_diag(void* THIS, void* EDX, char val)
{
	void* ret = _ReturnAddress();
	_fxsave(g_cz_fxbuf);
	DWORD c = (DWORD)ret;
	DWORD rva = (c >= baseAddressAI && c < baseAddressAI + 0x782000) ? (c - baseAddressAI + 0x10000000) : c;
	sprintf(buffer, "[CZT] SetUseTemplate=%d caller=0x%08X\n\0", (int)val, rva);
	Log(buffer);
	_fxrstor(g_cz_fxbuf);
	return org_SetUseTemplate_diag(THIS, EDX, val);
}

bool __fastcall IsDetachable_diag(void* THIS, void* EDX, int slot)
{
	bool result = org_IsDetachable_diag(THIS, EDX, slot);
	if (slot == 0)   // log once per customize draw (slot 0) -- avoids the per-slot flood
	{
		_fxsave(g_cz_fxbuf);
		int tmpl = THIS ? *((unsigned char*)THIS + 0xAC) : -1;
		sprintf(buffer, "[CZT] IsDetachable(slot0) bUseTemplate=%d -> %d\n\0", tmpl, (int)result);
		Log(buffer);
		_fxrstor(g_cz_fxbuf);
	}
	return result;
}

// ============================================================================
// REPLICATION-CALLBACK DIAGNOSTIC  (installed only when a "_rcdiag_" file exists)
// ----------------------------------------------------------------------------
// MAKE-OR-BREAK for the server-side A-pose/walk fix: does an inbound replicated-property update
// reach AI_EntityPlayer::ReplicationCallback @0x100CB24C on the LOCAL pawn, and does index 12
// (m_Mood -> AI_EntityHuman::RC case12 -> UpdateMood @0x10076E90) fire? The per-field RC dispatcher
// is in RDVDLL (Quazal DO layer), so confirm at RUNTIME. The local pawn is a SERVER-mastered SLAVE
// (owner=0x5c00002=DS, see memory gro-pawn-master-slave), so it SHOULD deserialize server updates +
// fire RC; this proves it. Read-only: the original is called FIRST (preserves the st0 double arg +
// runs UpdateMood unchanged for idx12); only the Log is fxsave/fxrstor-bracketed; gated to the LOCAL
// pawn (playerAddress) and non-12 indices are de-duped (first occurrence only) so it can't flood/lag.
// __thiscall(this=ecx, st0 double, int a2 on stack) -> __fastcall(this, edx-unused, a2). a2 is the
// replication property index. Offsets runtime-verified: m_Mood @pawn+0x824, prev-mood @pawn+0xDA8,
// load-state @pawn+0x1400 (3=banks loaded), gesture-mgr @pawn+0xF6C, dword5CC @mgr+0x5D4.
char (__fastcall* org_RC_diag)(void*, void*, int) = 0;
__declspec(align(16)) static unsigned char g_rc_fxbuf[512];
static unsigned char g_rc_seen[64] = { 0 };   // de-dup non-mood indices

char __fastcall RC_diag(void* THIS, void* EDX, int index)
{
	// Only the LOCAL player's pawn (avoid flood from bots / remote pawns / other entities).
	if (playerAddress == 0 || (DWORD)THIS != playerAddress)
		return org_RC_diag(THIS, EDX, index);

	DWORD c     = (DWORD)_ReturnAddress();
	DWORD mgr   = *(DWORD*)((char*)THIS + 0xF6C);
	DWORD descB = mgr ? *(DWORD*)(mgr + 0x5D4) : 0;     // dword5CC BEFORE
	DWORD mood  = *(DWORD*)((char*)THIS + 0x824);       // m_Mood.currValue
	DWORD mask  = *(DWORD*)((char*)THIS + 0xD90);       // ★ADS-LEVER: allowed-mood mask. Order_ChangeMood(n) is a NO-OP unless (1<<n)&mask. aiming = bit18 (0x40000)
	DWORD prev  = *(DWORD*)((char*)THIS + 0xDA8);       // prev-mood
	DWORD load  = *(DWORD*)((char*)THIS + 0x1400);      // visual load-state (3 = banks loaded)

	char result = org_RC_diag(THIS, EDX, index);        // run original (st0 intact) -> UpdateMood for idx12

	DWORD descA = mgr ? *(DWORD*)(mgr + 0x5D4) : 0;     // dword5CC AFTER

	bool doLog = (index == 12);
	if (!doLog && index >= 0 && index < 64 && !g_rc_seen[index]) { g_rc_seen[index] = 1; doLog = true; }
	if (doLog)
	{
		// caller as an AICLASS RVA when in-module (Order_ChangeMood ~0x1007ABxx = local), else raw
		// (an RDVDLL address == the DO-layer dispatcher == genuine server replication delivery).
		DWORD cr = (c >= baseAddressAI && c < baseAddressAI + 0x782000) ? (c - baseAddressAI + 0x10000000) : c;
		_fxsave(g_rc_fxbuf);
		sprintf(buffer, "[RC] idx=%d caller=0x%08X mood=0x%X prev=0x%X load=%d desc:0x%08X->0x%08X%s%s\n\0",
			index, cr, mood, prev, load, descB, descA,
			(index == 12) ? "  <MOOD>" : "",
			(descB == 0 && descA != 0) ? "  *** dword5CC SET ***" : "");
		Log(buffer);
		_fxrstor(g_rc_fxbuf);
	}
	return result;
}

// ============================================================================
// MOOD-ORDER DIAGNOSTIC  (installed only when a "_moodorder_" file exists)
// ----------------------------------------------------------------------------
// Finds WHAT triggers AI_EntityHuman::Order_ChangeMood @0x1007A9E0 (RVA 0x7A9E0) -- the LOCAL,
// engine-driven mood setter that resolves the A-pose (it cReplicatedFlag::Set/Remove's the mood bit
// @pawn+0x81C then calls UpdateMood, which sets dword5CC). RE (RE/plan/15) proved doc-14's m_Mood
// REPLICATION path is a dead end and that this Order_ChangeMood is a VIRTUAL (vtable slot +0xD8) with
// NO caller inside AICLASS (zero E8 xrefs; zero call[reg+0xD8]/[+0xE0] sites) -> it is invoked from
// ANOTHER MODULE (the Yeti engine) and RACES the locomotion driver (the ~10% spawn crash). This hook
// captures, for the LOCAL pawn: the caller's MODULE+offset (expected to be Yeti_Release.exe / AIDLL,
// NOT AICLASS), the newMood arg, the visual load-state, and the dword5CC 0->nonzero transition (the
// exact A-pose-resolve moment) -- so we can see the engine trigger + its timing vs the driver, then
// design a compliant server-side spawn-sequencing fix. READ-ONLY: the original runs FIRST (preserves
// the st0 double arg + does the real mood change/UpdateMood unchanged); only the Log is
// fxsave/fxrstor-bracketed; gated to the LOCAL pawn (playerAddress); de-duped by caller (each distinct
// caller logged once) + always logs the resolve transition, and capped, so it can't flood/lag spawn.
// Same convention as RC_diag: __userpurge(this=ecx, st0 double, int newMood on stack) -> __fastcall.
char (__fastcall* org_MoodOrder_diag)(void*, void*, int) = 0;
__declspec(align(16)) static unsigned char g_mo_fxbuf[512];
static DWORD g_mo_seenCaller[32] = { 0 };   // de-dup: distinct caller addresses already logged
static int   g_mo_seenCount = 0;
static int   g_mo_logged = 0;               // total lines emitted (cap to bound disk I/O)
static int   g_mo_seen18 = 0;               // one-shot: capture the first Order_ChangeMood(18) = the ADS aiming-mood attempt

char __fastcall MoodOrder_diag(void* THIS, void* EDX, int newMood)
{
	// NOTE (v2): NOT gated to playerAddress. The 1st test showed the in-match pawn resolves mood via
	// Order_ChangeMood BEFORE the per-frame hook latches playerAddress (so the old gate skipped it ->
	// zero [MO] lines). We instead log every DISTINCT caller once + every dword5CC resolve, mark the
	// LOCAL pawn when known, and rely on the de-dup(32)+cap(120) to stay bounded. THIS is always an
	// AI_EntityHuman here, so the field reads below are valid for any entity (mgr is null-checked).
	DWORD pa      = playerAddress;
	bool  isLocal = (pa != 0 && (DWORD)THIS == pa);

	DWORD c     = (DWORD)_ReturnAddress();              // the call site (RVA 0xD24A0 = AI_EntityPlayer::InitEntity)
	DWORD mgr   = *(DWORD*)((char*)THIS + 0xF6C);
	DWORD descB = mgr ? *(DWORD*)(mgr + 0x5D4) : 0;     // dword5CC BEFORE
	DWORD mood  = *(DWORD*)((char*)THIS + 0x824);       // m_Mood.currValue
	DWORD mask  = *(DWORD*)((char*)THIS + 0xD90);       // ★ADS-LEVER: allowed-mood mask. Order_ChangeMood(n) is a NO-OP unless (1<<n)&mask. aiming = bit18 (0x40000)
	DWORD prev  = *(DWORD*)((char*)THIS + 0xDA8);       // prev-mood
	DWORD load  = *(DWORD*)((char*)THIS + 0x1400);      // visual load-state (3 = banks loaded)
	DWORD sflag = *(DWORD*)((char*)THIS + 0x30);        // serializationFlags (&1=master/local, &2=server-authored) -- gates InitEntity's Order_ChangeMood(3)
	DWORD owner = *(DWORD*)((char*)THIS + 0x6C);        // playerStationID = the create-message 'owner' (SetMasterFlag stored it)
	// AI_NetworkManager::Instance @ global RVA 0x6ACBA4; myNetId @ Inst+0x170, InternalOwnerId @ Inst+0x16B.
	// IsMaster(in-session) = (myNetId == owner); so to reliably set &1 the create owner must == myNetId.
	DWORD nmInst = *(DWORD*)(baseAddressAI + 0x6ACBA4);
	DWORD myNet  = nmInst ? *(DWORD*)(nmInst + 0x170) : 0;
	DWORD intOwn = nmInst ? (*(BYTE*)(nmInst + 0x16B) ? 1u : 0u) : 0;

	char result = org_MoodOrder_diag(THIS, EDX, newMood);   // run original (st0 intact) -> mood change + UpdateMood

	DWORD descA = mgr ? *(DWORD*)(mgr + 0x5D4) : 0;     // dword5CC AFTER

	// Log (a) the first time each distinct caller is seen (to enumerate the engine trigger sites), and
	// (b) ALWAYS the resolve transition dword5CC 0->nonzero (the A-pose-fixed moment + its timing).
	bool resolve   = (descB == 0 && descA != 0);
	bool aim18     = (newMood == 18 && !g_mo_seen18);   // ADS aiming-mood attempt (bit18=0x40000); log once regardless of caller de-dup
	if (aim18) g_mo_seen18 = 1;
	bool newCaller = true;
	for (int i = 0; i < g_mo_seenCount; i++) if (g_mo_seenCaller[i] == c) { newCaller = false; break; }
	if (newCaller && g_mo_seenCount < 32) g_mo_seenCaller[g_mo_seenCount++] = c;

	if ((newCaller || resolve || aim18) && g_mo_logged < 120)
	{
		g_mo_logged++;
		_fxsave(g_mo_fxbuf);
		// Resolve the caller to module+offset (it is expected to be a Yeti-engine address, NOT AICLASS).
		// UNCHANGED_REFCOUNT so we never leak a module ref; de-dup keeps these Win32 calls bounded.
		char modname[64] = "?";
		DWORD modoff = c;
		HMODULE hmod = 0;
		if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
			(LPCSTR)c, &hmod) && hmod)
		{
			char full[260];
			DWORD n = GetModuleFileNameA(hmod, full, sizeof(full));
			if (n > 0)
			{
				// basename: keep everything after the last path separator (no CRT str* dependency)
				DWORD start = 0, j = 0;
				for (DWORD i = 0; i < n; i++) if (full[i] == '\\' || full[i] == '/') start = i + 1;
				for (DWORD i = start; i < n && j < sizeof(modname) - 1; i++) modname[j++] = full[i];
				modname[j] = 0;
			}
			modoff = c - (DWORD)hmod;
		}
		sprintf(buffer, "[MO] %s this=0x%08X newMood=%d caller=0x%08X (%s+0x%X) sflag=0x%X mood=0x%X mask=0x%X aimBitInMask=%d load=%d desc:0x%08X->0x%08X%s\n\0",
			isLocal ? "LOCAL" : "ent  ", (DWORD)THIS, newMood, c, modname, modoff, sflag, mood, mask, ((mask & 0x40000) ? 1 : 0), load, descB, descA,
			resolve ? "  *** dword5CC SET (A-POSE RESOLVE) ***" : "");
		Log(buffer);
		_fxrstor(g_mo_fxbuf);
	}
	return result;
}

// [CAM] ADS camera-mode diag (installed with "_moodorder_"): hook AI_Camera_SelectStanceCameraMode @0x101B6F20.
// Logs ONLY when aiming (mood&0x40000), capped. byte969 (pawn+0x144D) gates aim mode 8 (correct ADS) vs the
// fall-through aim-down mode 3 (wrong). Splits: byte969=0 -> mode 3 (deploy/input-focus flag never latched) vs
// byte969=1 & selectedMode!=8 -> the wrongness is downstream (camera-settings table / iron remap).
void* (__cdecl* org_ADSmode_diag)(int, void*) = 0;
static int g_adsmode_n = 0;
int g_force969 = 0;   // [F969] set from FileExists("_force969_") in DetourMain: when 1, force byte969=1 while aiming
int g_forcemode3 = 0; // [FM3] set from FileExists("_forcemode3_"): when 1, force base camera mode 3 (->IronMode 21) while aiming
int g_forcefov = 0;   // [FFOV] set from FileExists("_forcefov_"): when 1, force FOV=50deg while ironF>0.7 (ADS zoom test)
int g_forceironfov = 0; // [FIFOV] set from FileExists("_forceironfov_"): force the weapon iron-FOV blend target (weapon+1192)=50deg
float g_eyeback = 0.0f;  // [EYEBACK] meters to pull the rendered eye BACK along the aim heading at full ADS (0=off); from _eyeback_ file content
float g_lastIronF = 0.0f; // [EYEBACK3] latest iron-sight factor, stored by IronFactor_diag (the render-push hook can't derive it from its node-binding THIS)
DWORD g_cameraGAO = 0;    // [EYEBACK4] the RENDER camera GAO (AI_CameraBase[5] = *(AI_CameraBase+20), bound to the viewport via CAM_AssignViewportCamToMe), stored by IronFactor_diag
int   g_wcptOff = 0;       // [WCPTFORCE] legacy single-pair (kept for Source.cpp startup parse; live re-read fills the arrays)
float g_wcptVal = 0.0f;
int   g_wcptOffA[4] = { 0, 0, 0, 0 };   // [WCPTFORCE] up to 4 simultaneous live knobs from _wcptforce_ ("<off> <val> <off> <val> ...")
float g_wcptValA[4] = { 0, 0, 0, 0 };   //   off>=1024: weapon byte-offset (e.g. 1216=scopeKey, 1192=ironFOV target); off<1024: ranged PropertyList propID (e.g. 76=Scope_IronSightOffsetY)
int   g_wcptN = 0;
int   g_scopefam = 0;      // [SCOPEFAM] set from FileExists("_scopefam_"): force the swap-scope GAO's viewport families to 0xFFFF while aiming (family-mapping test)
void* __cdecl ADSmode_diag(int a1, void* a2)
{
	// 2026-06-08 NOTE: an UNGATED byte969 force "did NOT change standing ADS (and broke prone)". RE-TEST
	// 2026-06-11 (clean, AIMING-GATED): force byte969=1 ONLY while aiming so SelectStanceCameraMode picks aim
	// mode 8 (vs the mode-3 third-person fallback) WITHOUT touching prone/other stances. Enabled by a
	// "_force969_" file (g_force969). Integer-only, BEFORE the original reads byte969. Decisive test of whether
	// mode 8 == first-person ADS (settles the 06-08 vs 06-10 contradiction). Delete "_force969_" to revert.
	if (g_force969 && a2)
	{
		DWORD fm = *(DWORD*)((char*)a2 + 0x824);       // m_Mood.currValue
		if (fm & 0x40000)                              // aiming bit only -> prone/other stances untouched
			*(unsigned char*)((char*)a2 + 0x144D) = 1; // byte969 = 1 (forces aim mode 8)
	}
	void* r = org_ADSmode_diag(a1, a2);               // run original (writes the mode to *a1)
	// [FM3] override the consumed BASE mode to 3 (standing-aim) while aiming, so the downstream IronMode remap
	// (sub_101B7730, gated dword480==2) makes it 21 -- testing whether the AIM-stance iron camera CENTERS the
	// eye (vs the idle-stance mode 19 that the gesture-40/replicated-z divert currently selects). _forcemode3_ gated.
	if (g_forcemode3 && a1 && (*(DWORD*)((char*)a2 + 0x824) & 0x40000))
		*(int*)a1 = 3;
	DWORD mood = *(DWORD*)((char*)a2 + 0x824);
	if ((mood & 0x40000) && g_adsmode_n < 30)         // only while aiming
	{
		g_adsmode_n++;
		_fxsave(g_mo_fxbuf);
		DWORD b969 = *(BYTE*)((char*)a2 + 0x144D);     // byte969 (the aim-mode gate)
		int   mode = a1 ? *(int*)a1 : -1;              // the mode SelectStanceCameraMode just wrote
		DWORD d480 = *(DWORD*)((char*)a2 + 0x480);     // OTS/iron state (2=iron)
		sprintf(buffer, "[CAM] AIM byte969=%u selectedMode=%d mood=0x%X d480=%u\n\0", b969, mode, mood, d480);
		Log(buffer);
		_fxrstor(g_mo_fxbuf);
	}
	return r;
}

// [BLITZFIX] Blitz-deploy crash containment + diagnostic. Flag-gated by "_blitzfix_" (Source.cpp DetourMain).
// cGestureMix::StretchRosace @0x100eb1e0 runs rev_SendErrorMessage + __debugbreak (an assert) when the anim
// stretch coefficient is <= 0.1 or >= 10.0. Activating Blitz sets mood bit 16 -> selects rosace descriptor 17,
// whose loaded +44 stretch coef is out of that range -> crash the instant the shield deploys. We CLAMP an
// out-of-range coef to a neutral 1.0 so the assert passes and Blitz deploys normally, and LOG the real coef so
// the root cause is pinned (0/garbage => wrong/uninitialized descriptor; a real extreme => bad anim DATA). The
// clamp is PURE INTEGER bit-math (no FP) so it cannot perturb the gesture/anim x87/SSE state; only the rare %f
// log is _fxsave-bracketed. 0.1f=0x3DCCCCCD, 10.0f=0x41200000, 1.0f=0x3F800000 (positive IEEE-754 bits are
// monotonic with magnitude; the sign/NaN/inf cases also clamp). Delete "_blitzfix_" to revert to native behavior.
__declspec(align(16)) static unsigned char g_blitz_fxbuf[512];
const char* (__fastcall* org_StretchRosace_blitzfix)(void*, void*, float) = 0;
static int g_blitz_n = 0;
int g_blitzclamp = 0;   // [BLITZFIX]  set from FileExists("_blitzfix_"): clamp the coef so Blitz deploys
int g_blitzprobe = 0;   // [BLITZPROBE] set from FileExists("_blitzprobe_"): log the decisive deploy/bank-load state
const char* __fastcall StretchRosace_blitzfix(void* THIS, void* EDX, float a2)
{
	DWORD bits = *(DWORD*)&a2;
	DWORD mag  = bits & 0x7FFFFFFF;
	int clampIt = (bits & 0x80000000) || (mag <= 0x3DCCCCCDu) || (mag >= 0x41200000u);
	float safe;
	if (clampIt) *(DWORD*)&safe = 0x3F800000;   // out of (0.1,10) [or neg/NaN/inf] -> neutral 1.0
	else         *(DWORD*)&safe = bits;          // in range -> pass through unchanged
	// [BLITZPROBE2] read-only DECISIVE capture on the OUT-OF-RANGE (crashing) rosace. Probe-1 PROVED the
	// descriptor/gesture-Set/banks are all correct (id41=17, str44=1.0, set 0, banks=0x7F) and that the garbage
	// coef is COMPUTED by sub_100E73C0 from the rosace MOVEMENT-DIRECTION ANGLE at cGestureMix+0x6E0 (config
	// floats are sane), where +0x6E0 is copied from masterPlayer+0x71C. cGestureMix is embedded at
	// AI_GestureMixManager+0x8 and masterPlayer at AI_GestureMixManager+0x4, so masterPlayer = *(THIS-4)
	// (verified at the sub_101F0420 call site: lea edi,[esi+8]; fld [eax+71Ch]). Capture the angle/speed (the
	// gesturemix copy AND the player source), the path/case selectors (byte+0xDA0 picks the directional path
	// that reads the angle; byte+0x818==2 is the AI_EntityHuman::Replica case that (re)computes +0x718 and the
	// +0x700 direction vector), the computed direction vector (+0x700) and orientation flags (+0x750).
	// DECODE: f6E0/ang71C garbage => the angle is bad (the proven root). sw818!=2 => the Replica rosace-update
	// case never ran for this entity (fields stale/uninit) -> server lever = drive that locomotion sub-state.
	// sw818==2 but dir700/ang71C garbage => the case ran but the movement/orientation inputs are garbage ->
	// server lever = fix the movement/orientation replica. selDA0 will read !=2 (we are on the crash path).
	if (g_blitzprobe && clampIt)
	{
		_fxsave(g_blitz_fxbuf);
		DWORD f6E0 = *(DWORD*)((char*)THIS + 0x6E0);   // gesturemix angle copy (the sub_100E73C0 input)
		DWORD f6E4 = *(DWORD*)((char*)THIS + 0x6E4);   // gesturemix speed copy
		DWORD desc = *(DWORD*)((char*)THIS + 0x5CC);
		DWORD id41 = (desc >= 0x10000 && desc < 0x80000000) ? *(unsigned char*)(desc + 41) : 0xFF;
		DWORD mp   = *(DWORD*)((char*)THIS - 4);        // AI_GestureMixManager = THIS-8; masterPlayer @ +4
		DWORD selDA0 = 0xFFFFFFFF, sw818 = 0xFFFFFFFF, ang71C = 0, spd718 = 0, d0 = 0, d1 = 0, d2 = 0, fl750 = 0;
		if (mp >= 0x10000 && mp < 0x80000000)
		{
			selDA0 = *(unsigned char*)(mp + 0xDA0);    // path selector (!=2 => directional/crash path)
			sw818  = *(unsigned char*)(mp + 0x818);    // Replica switch (==2 => rosace-update case ran)
			ang71C = *(DWORD*)(mp + 0x71C);            // source angle (should == f6E0)
			spd718 = *(DWORD*)(mp + 0x718);            // source speed (should == f6E4)
			d0 = *(DWORD*)(mp + 0x700); d1 = *(DWORD*)(mp + 0x704); d2 = *(DWORD*)(mp + 0x708);  // direction vec
			fl750 = *(DWORD*)(mp + 0x750);            // orientation flags
		}
		sprintf(buffer, "[BLITZPROBE2] coefBits=0x%08X f6E0=0x%08X f6E4=0x%08X id41=%u mp=0x%08X selDA0=%u sw818=%u ang71C=0x%08X spd718=0x%08X dir700=(0x%08X,0x%08X,0x%08X) flags750=0x%08X this=0x%08X\n",
			bits, f6E0, f6E4, id41, mp, selDA0, sw818, ang71C, spd718, d0, d1, d2, fl750, (DWORD)THIS);
		Log(buffer);
		_fxrstor(g_blitz_fxbuf);
	}
	if ((g_blitzclamp && clampIt) || g_blitz_n < 24)
	{
		if (g_blitz_n < 1000) g_blitz_n++;
		_fxsave(g_blitz_fxbuf);
		sprintf(buffer, "[BLITZFIX] StretchRosace coef=%f (bits=0x%08X) %s clamp=%d -> %f  this=0x%08X\n",
			a2, bits, clampIt ? "OUT-OF-RANGE" : "ok", g_blitzclamp, safe, (DWORD)THIS);
		Log(buffer);
		_fxrstor(g_blitz_fxbuf);
	}
	// clamp only if _blitzfix_ is armed; probe-only run passes the original (crashing) coef through (read-only)
	return org_StretchRosace_blitzfix(THIS, EDX, g_blitzclamp ? safe : a2);
}

// [IRONF] iron-sight BLEND factor diag (installed with "_moodorder_"): hook AI_CameraBase::GetIronSightFactor
// @0x101B0760. Returns *(this[11]+212): 1.0(0x3F800000)=full iron-sight position, 0.0=full over-the-shoulder.
// If this stays 0 while aiming, the camera POSITION never blends to the iron eye-offset -> "sights view but
// camera in the wrong (over-shoulder) spot" -- exactly the reported symptom. Logs the raw factor on change, capped.
double (__fastcall* org_IronFactor_diag)(void*, void*) = 0;
static int   g_ironf_n = 0;
static DWORD g_ironf_last = 0xFFFFFFFF;
double __fastcall IronFactor_diag(void* THIS, void* EDX)
{
	double r = org_IronFactor_diag(THIS, EDX);
	DWORD ctx = ((DWORD*)THIS)[11];
	DWORD f = ctx ? *(DWORD*)(ctx + 212) : 0xDEADBEEF;
	if (ctx) g_lastIronF = *(float*)&f;   // [EYEBACK3] feed the render-push hook the live iron factor (same frame, before the matrix push)
	g_cameraGAO = *(DWORD*)((DWORD)THIS + 20);   // [EYEBACK4] this AI_CameraBase's bound viewport camera GAO (this[5])
	// [ADSFIX v3] CORRECTED after the static-RE audit. player+0xFA0 is NOT the camera wrapper -- it is a pointer to
	// the VALUE SLOT of the pawn's "Camera" net-property; the AI_CameraBase wrapper is **(player+0xFA0) (double
	// deref, cf. AI_EntityPlayer::GetCameraBase @0x100CE650 = mov eax,[ecx+0FA0h]; mov eax,[eax]). v2's single-deref
	// +44 write was corrupting property-record heap and its gate never passed (REMOVED -- no pointer writes at all;
	// the wrapper is fully wired by retail InitEntity and ticked per frame; sub_101B0D50 already runs on it).
	// The fix proper: AI_EntityPawn::UpdateWeaponOpacity @0x100DB8E0 (the ADS fades: weapon body fade-OUT
	// ramp(ironF,1.0,0.7) x player+0xEF8, swap-scope SIGHT fade-IN ramp(ironF,0.7,0.9) RAW) is normally driven from
	// AI_EntityPawn::Replica (which runs from BOTH Master and Replica dispatch) -- if that chain is dead on the
	// emulator the fades never apply. Driving it here is exact-retail and idempotent (pure function of ironF/ch3),
	// so it is safe whether or not the engine also calls it. Reentrancy guard: it calls GetIronSightFactor twice.
	{
		static int   g_adsfix_busy = 0;
		static int   g_adsfix_n = 0;
		static DWORD g_adsfix_tick = 0;
		DWORD nowT = GetTickCount();
		if (!g_adsfix_busy && playerAddress && (nowT - g_adsfix_tick) >= 10)   // ~once per frame
		{
			g_adsfix_tick = nowT;
			DWORD slot = *(DWORD*)(playerAddress + 0xFA0);                     // "Camera" property value slot
			DWORD wrap = 0;
			if (slot >= 0x00010000 && slot < 0x7FFF0000 && (slot & 3) == 0)
				wrap = *(DWORD*)slot;                                          // the actual AI_CameraBase wrapper
			if (wrap >= 0x00010000 && wrap < 0x7FFF0000 && (wrap & 3) == 0 && *(DWORD*)(wrap + 44))
			{
				g_adsfix_busy = 1;
				_fxsave(g_mo_fxbuf);
				((void(__fastcall*)(void*, void*))(baseAddressAI + 0xDB8E0))((void*)playerAddress, 0); // AI_EntityPawn::UpdateWeaponOpacity
				// [SCOPEFAM] family test (gated by _scopefam_): the scope is attached, positioned at the eye, meshed,
				// and opacity-1 at full ADS yet invisible -- the last suspect is the ADS render family 0x80 not being
				// drawn by the displayed viewport. Force the scope's families to 0xFFFF (every layer) while aiming:
				// if the sight pops in, family mapping is the bug (then find what enables family 0x80 in retail).
				if (g_scopefam && g_lastIronF > 0.3f)
				{
					DWORD wpnF = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)playerAddress, 0);
					if (wpnF >= 0x00010000 && wpnF < 0x7FFF0000 && (wpnF & 3) == 0)
					{
						DWORD sgF = *(DWORD*)(wpnF + 0x4BC);
						if (sgF >= 0x00010000 && sgF < 0x7FFF0000 && (sgF & 3) == 0)
							((void(__cdecl*)(DWORD, unsigned int, unsigned int))(baseAddressAI + 0x54870))(sgF, 0xFFFF, 0xFFFF); // AIDLL::OBJ_SetViewportFamilies
					}
				}
				_fxrstor(g_mo_fxbuf);
				g_adsfix_busy = 0;
				if (g_adsfix_n < 1)
				{
					g_adsfix_n++;
					_fxsave(g_mo_fxbuf);
					sprintf(buffer, "[ADSFIX] v3: UpdateWeaponOpacity driven (wrapper=%08X via double-deref)\n", wrap);
					Log(buffer);
					_fxrstor(g_mo_fxbuf);
				}
			}
		}
	}
	// [WCPTFORCE] LIVE-tunable: re-read _wcptforce_ every ~3s so the value can be dialed in WHILE THE GAME RUNS
	// (edit the file, alt-tab back, see it within seconds -- no restarts). Empty/missing/invalid file = force off.
	{
		static DWORD g_wf_lastTick = 0;
		DWORD wfTick = GetTickCount();
		if (wfTick - g_wf_lastTick > 3000)
		{
			g_wf_lastTick = wfTick;
			_fxsave(g_mo_fxbuf);
			int nN = 0; int nOff[4] = { 0,0,0,0 }; float nVal[4] = { 0,0,0,0 };
			FILE* wfp = fopen("_wcptforce_", "r");
			if (wfp)
			{
				char wb[128] = { 0 };
				size_t wn = fread(wb, 1, 127, wfp);
				fclose(wfp);
				if (wn > 127) wn = 127;
				wb[wn] = 0;
				char* wp2 = wb;
				while (nN < 4)
				{
					char* wend = 0;
					long po = strtol(wp2, &wend, 10);
					if (wend == wp2) break;
					wp2 = wend;
					float pv = (float)strtod(wp2, &wend);
					if (wend == wp2) break;
					wp2 = wend;
					if (po > 0 && po < 2400) { nOff[nN] = (int)po; nVal[nN] = pv; nN++; }
				}
			}
			int changed = (nN != g_wcptN);
			for (int ci = 0; ci < 4 && !changed; ci++) changed = (nOff[ci] != g_wcptOffA[ci]) || (nVal[ci] != g_wcptValA[ci]);
			if (changed)
			{
				g_wcptN = nN;
				char* lp = buffer;
				lp += sprintf(lp, "[WCPTFORCE] live update (%d knobs):", nN);
				for (int ci = 0; ci < 4; ci++)
				{
					g_wcptOffA[ci] = nOff[ci]; g_wcptValA[ci] = nVal[ci];
					if (ci < nN) lp += sprintf(lp, "  %s %d = %.4f", (nOff[ci] >= 1024) ? "wpn+" : "prop", nOff[ci], nVal[ci]);
				}
				sprintf(lp, "\n");
				Log(buffer);
			}
			_fxrstor(g_mo_fxbuf);
		}
	}
	// per-frame force while aiming, up to 4 knobs ("<target> <value>" pairs):
	//   target >= 1024: raw weapon byte-offset (1192 = iron-FOV blend target; 1216 = scopeKey -- "1216 0" KILLS the scope
	//                   object so the camera anchor falls back to the weapon's OWN iron line (sub_101B93E0), the test for
	//                   "the emulator scope GAO's eye joint sits mid-gun");
	//   target <  1024: PROPERTY ID in the ranged PropertyList @ weapon+0x28 (record propID @ +0xC, value @ +20) --
	//                   76 = WCPT_Scope_IronSightOffsetY_F (eye sits THIS far behind the iron joint; 0.2 now, rifle ~0.5).
	if (g_wcptN > 0 && playerAddress && g_lastIronF > 0.01f)
	{
		_fxsave(g_mo_fxbuf);
		DWORD wpf = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)playerAddress, 0);
		if (wpf >= 0x00010000 && wpf < 0x7FFF0000 && (wpf & 3) == 0)
		{
			for (int ki = 0; ki < g_wcptN; ki++)
			{
				int ko = g_wcptOffA[ki];
				int koProp = -1, koByte = 0;
				if (ko >= 2000 && ko < 2400) { koProp = ko - 2000; koByte = 1; }   // "2019 1" = prop 19 as BYTE (e.g. GunHidden_B)
				else if (ko > 0 && ko < 1024) koProp = ko;                          // float property force
				if (ko >= 1024 && ko <= 1500)
				{
					*(float*)(wpf + ko) = g_wcptValA[ki];
				}
				else if (koProp >= 0)
				{
					DWORD pcount = *(DWORD*)(wpf + 0x28 + 8);
					DWORD pbase  = *(DWORD*)(wpf + 0x28 + 0xC);
					if (pbase >= 0x00010000 && pbase < 0x7FFF0000 && pcount < 400)
					{
						for (DWORD pi = 0; pi < pcount; pi++)
						{
							DWORD rec = pbase + 24 * pi;
							if (*(DWORD*)(rec + 0xC) == (DWORD)koProp)
							{
								if (koByte) *(unsigned char*)(rec + 20) = (unsigned char)g_wcptValA[ki];
								else        *(float*)(rec + 20) = g_wcptValA[ki];
								break;
							}
						}
					}
				}
			}
		}
		_fxrstor(g_mo_fxbuf);
	}
	if (f != g_ironf_last && g_ironf_n < 40)          // log on change only (bounded)
	{
		g_ironf_last = f;
		g_ironf_n++;
		_fxsave(g_mo_fxbuf);
		// [IRONEYE] the iron-sight EYE OFFSET the camera blend (sub_101B2F10) pulls toward, computed per-frame by
		// sub_101BA110 (scope branch: scope 'eye' joint; iron branch: weapon iron joint - GetProperty(76)) into
		// AI_Camera+1380..1388. ZERO at full ADS = the engage path is broken (scope GAO missing its joints / branch
		// skipped) => camera stays at the stance eye = the symptom. +492 = the blend's ironF copy (cam+212 mirrored).
		DWORD ie0 = *(DWORD*)(ctx + 1380), ie1 = *(DWORD*)(ctx + 1384), ie2 = *(DWORD*)(ctx + 1388);
		DWORD f492 = *(DWORD*)(ctx + 492);
		// [ADSRIG] the FP-rig object at player+0xFA0 (player[1].propModifier_21_CombatProperties.properties.dword0 in
		// sub_100C9580): the ADS-enter engage hooks [rig+56] to the SCOPE 'eye' joint (hash 80395192) and [rig+68] to the
		// weapon body joint -- THE mechanism that puts the sight at the camera. rig==0 => hooks silently skipped => arms
		// detach but the gun stays in the hip pose = "looking over the gun". Logged per ironF change.
		// [IRONF] probe: wrap = **(player+0xFA0) (the REAL camera wrapper, double deref -- expect == THIS); ch0..ch3 =
		// the occlusion/opacity channels player+0xEEC/0xEF0/0xEF4/0xEF8, written per frame by AI_EntityPawn::Replica
		// (SetCameraView sets ch0..2=0, ch3=1 at ironF>0.8). At full ADS: ch0..2==0 => the Replica chain RUNS on our
		// build; ch0..2 stuck at 1.0 => the pawn's Update->Master->Pawn::Replica dispatch is dead (then [ADSFIX]'s
		// driven UpdateWeaponOpacity is the only fade source).
		DWORD wrap2 = 0;
		DWORD ch0 = 0, ch1 = 0, ch2 = 0, ch3 = 0;
		// [SCOPE] the three sub-opacity failure modes, distinguished live: hk56 = GAO currently hooked to camera
		// hook slot 1 (cBlobShadowManager::GetHookInterface(wrap+56) -- should equal the swap-scope GAO during ADS,
		// 0 = attach never happened); gobj = VIS_uc_GetGraphicObjectCount(scope) (0 = model never async-loaded =>
		// opacity applies to nothing); spos = scope world pos (far from the camera => not positioned/driven).
		DWORD hooked56 = 0, scopeGAO2 = 0; int hkIdx = -1, gobj = -1;
		float spos[3] = { 0, 0, 0 };
		if (playerAddress)
		{
			DWORD slot2 = *(DWORD*)(playerAddress + 0xFA0);
			if (slot2 >= 0x00010000 && slot2 < 0x7FFF0000 && (slot2 & 3) == 0)
				wrap2 = *(DWORD*)slot2;
			ch0 = *(DWORD*)(playerAddress + 0xEEC);
			ch1 = *(DWORD*)(playerAddress + 0xEF0);
			ch2 = *(DWORD*)(playerAddress + 0xEF4);
			ch3 = *(DWORD*)(playerAddress + 0xEF8);
			if (wrap2 >= 0x00010000 && wrap2 < 0x7FFF0000 && (wrap2 & 3) == 0)
			{
				hkIdx = *(unsigned char*)(wrap2 + 64);   // holder@+56 hook index byte (+8); 0xFF = never bound
				hooked56 = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x2EC10))((void*)(wrap2 + 56), 0); // Holder::GetHooked
			}
			DWORD wpn3 = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)playerAddress, 0);
			if (wpn3 >= 0x00010000 && wpn3 < 0x7FFF0000 && (wpn3 & 3) == 0)
			{
				scopeGAO2 = *(DWORD*)(wpn3 + 0x4BC);
				if (scopeGAO2 >= 0x00010000 && scopeGAO2 < 0x7FFF0000 && (scopeGAO2 & 3) == 0)
				{
					gobj = (unsigned char)((char(__cdecl*)(DWORD))(baseAddressAI + 0x62700))(scopeGAO2);   // VIS_uc_GetGraphicObjectCount
					((float*(__cdecl*)(float*, int))(baseAddressAI + 0x12D30))(spos, (int)scopeGAO2);      // OBJ_vGetPos
				}
			}
		}
		// [SREL] scope position relative to the RENDER EYE, same frame: fwd = how far the scope GAO origin sits IN
		// FRONT of the camera along the aim heading (NEGATIVE = behind the camera = invisible, the user's hypothesis);
		// up = height vs the eye. Eye = AI_Camera final eye ctx+472..480; heading = OBJ_vGetYaxis(pawn GAO).
		float srelF = -999.0f, srelU = -999.0f;
		if (ctx && playerAddress && scopeGAO2)
		{
			DWORD ce0 = *(DWORD*)(ctx + 472), ce1 = *(DWORD*)(ctx + 476), ce2 = *(DWORD*)(ctx + 480);
			DWORD pg2 = *(DWORD*)(playerAddress + 0x14);
			if (pg2 >= 0x00010000 && pg2 < 0x7FFF0000 && (pg2 & 3) == 0)
			{
				float hYp[3] = { 0, 0, 0 };
				((float*(__cdecl*)(float*, int))(baseAddressAI + 0x72D90))(hYp, (int)pg2);   // OBJ_vGetYaxis = aim heading
				srelF = (spos[0] - *(float*)&ce0) * hYp[0] + (spos[1] - *(float*)&ce1) * hYp[1];
				srelU = spos[2] - *(float*)&ce2;
			}
		}
		// [OVERLAY] the HUD scope-overlay gate (cPlayerHUD::CrossHair_Update): overlay shows iff adsBlend>0.9 AND
		// player+0x480 == 2 (the ADS view-mode enum; setter vmt+0x214 has no in-DLL callers = script/engine-driven)
		// AND bIsGunHidden(weapon). m480 != 2 with an overlay scope equipped = the mode never gets set (next lever).
		int m480 = -1, gunHid = -1;
		if (playerAddress)
		{
			m480 = *(int*)(playerAddress + 0x480);
			DWORD wpn4 = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)playerAddress, 0);
			if (wpn4 >= 0x00010000 && wpn4 < 0x7FFF0000 && (wpn4 & 3) == 0)
				gunHid = (unsigned char)((char(__fastcall*)(void*, void*))(baseAddressAI + 0x642C0))((void*)wpn4, 0); // bIsGunHidden
		}
		sprintf(buffer, "[IRONF] ironF=0x%08X ch=(%.2f,%.2f,%.2f,%.2f) scope=%08X hk56=%08X gobj=%d srel=(fwd %.3f, up %.3f) m480=%d gunHid=%d\n",
			f, *(float*)&ch0, *(float*)&ch1, *(float*)&ch2, *(float*)&ch3,
			scopeGAO2, hooked56, gobj, srelF, srelU, m480, gunHid);
		Log(buffer);
		// [ZOPT] one-shot dump of the zen iron-sight camera-options block (sub_101FE810()+88) that sub_101B9830 builds
		// the iron camera FRAME from (+0x00..0x2C base vectors, +0x30/+0x34 scalars, +0x3C/40/44 offset scale,
		// +0x48/4C/50 stance/scope/jump factors). ALL-ZERO = the options never loaded = iron frame collapses to the
		// stance eye = "camera never travels to the gun" root cause.
		{
			static int g_zopt_n = 0;
			if (g_zopt_n < 1)
			{
				g_zopt_n++;
				DWORD zo = ((DWORD(__cdecl*)())(baseAddressAI + 0x1FE810))();
				if (zo >= 0x00010000 && zo < 0x7FFF0000)
				{
					DWORD zb = *(DWORD*)(zo + 88);
					if (zb >= 0x00010000 && zb < 0x7FFF0000)
					{
						char* zp = buffer;
						zp += sprintf(zp, "[ZOPT] blk=%08X:", zb);
						for (int zi = 0; zi < 24; zi++) zp += sprintf(zp, " %08X", *(DWORD*)(zb + zi * 4));
						sprintf(zp, "\n");
						Log(buffer);
					}
					else { sprintf(buffer, "[ZOPT] blk INVALID (%08X)\n", zb); Log(buffer); }
				}
			}
		}
		_fxrstor(g_mo_fxbuf);
	}
	return r;
}

// [VGP] REAL rendered camera position via a SAFE hook on AI_CameraBase::vGetPos (0x101B0E90, __thiscall(this, out)).
// The earlier crash came from CALLING vGetPos mid-ApplyStanceCameraSettings (the camera matrix was being rebuilt);
// this HOOKS it instead -- the GAME calls vGetPos at its own safe time and we just read the filled output (out =
// the camera world-matrix translation). Logs the camera world pos vs the iron-sight factor (camera ctx[11]+212) on
// factor-change, capturing the hip<->iron camera trajectory. Read-only, capped, FP-safe; no mid-update call; the
// ctx pointer is range-guarded before the factor read.
float* (__fastcall* org_vGetPos_diag)(void*, void*, float*) = 0;
static int   g_vgp_n = 0;
static DWORD g_vgp_lastF = 0xFFFFFFFF;
float* __fastcall vGetPos_diag(void* THIS, void* EDX, float* out)
{
	float* r = org_vGetPos_diag(THIS, EDX, out);   // game fills `out` = camera world position (safe call timing)
	if (out && THIS && g_vgp_n < 140)
	{
		DWORD ctx = ((DWORD*)THIS)[11];            // AI_CameraBase context (GetIronSightFactor's value lives @ ctx+212)
		if (ctx >= 0x00010000 && ctx < 0x7FFF0000 && (ctx & 3) == 0)
		{
			DWORD fb = *(DWORD*)(ctx + 212);       // ironSightFactor raw bits (0 = hip/over-shoulder, 0x3F800000 = full iron)
			if (fb != g_vgp_lastF)                 // log only on factor change -> the hip<->iron ramp
			{
				g_vgp_lastF = fb;
				g_vgp_n++;
				_fxsave(g_mo_fxbuf);
				sprintf(buffer, "[VGP] ironF=%.3f camPos=(%.3f, %.3f, %.3f)\n", *(float*)&fb, out[0], out[1], out[2]);
				Log(buffer);
				// [GEOM] same-frame body-relative camera read. Resolve the local pawn (playerAddress, set by the
				// UpdateWarning hook), then engine getters: OBJ_vGetPos @0x12D30 (GAO feet world pos) + OBJ_vGetYaxis
				// @0x72D90 (heading Y-axis). Offline: project (cam-feet) onto headY -> fwd/back (camera BEHIND = OTS),
				// perpendicular -> lateral (over-shoulder), z-(feet.z) -> eye height. Splits "camera slung
				// over-the-shoulder (camera bug)" vs "camera at the eye (=> the GUN/aim-pose is the culprit)".
				if (playerAddress)
				{
					DWORD pgao = *(DWORD*)(playerAddress + 0x14);   // pawn->gameObject (GAO)
					if (pgao >= 0x00010000 && pgao < 0x7FFF0000 && (pgao & 3) == 0)
					{
						float feet[3] = { 0, 0, 0 }, hY[3] = { 0, 0, 0 };
						((float*(__cdecl*)(float*, int))(baseAddressAI + 0x12D30))(feet, (int)pgao);  // OBJ_vGetPos
						((float*(__cdecl*)(float*, int))(baseAddressAI + 0x72D90))(hY,   (int)pgao);  // OBJ_vGetYaxis
						// [GUN] weapon iron-sight WORLD pos (SetCameraView recipe): GetWeaponComponent(pawn) ->
						// weapon+912 (joint handle) / +916 (subIdx) -> zen::GetJointGlobal (0xD96C0) writes the joint
						// matrix; world translation is at floats [4]/[5]/[6] (per sub_100D9250). Splits "eye sits
						// beside the sight line (camera 0.20m lateral)" vs "gun held LOW (aim-pose)": compare gun.z to
						// cam.z (eye height) and gun vs cam along/perp to heading.
						float gun[3] = { 0, 0, 0 }; int gunOK = 0;
						DWORD ironFov84 = 0xFFFFFFFF, ironFov92 = 0xFFFFFFFF;  // weapon iron-FOV fields (degrees, raw bits)
						DWORD wpn = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)playerAddress, 0);
						if (wpn >= 0x00010000 && wpn < 0x7FFF0000 && (wpn & 3) == 0)
						{
							ironFov84 = *(DWORD*)(wpn + 1184);   // WCPT_IronSight_FOV (sub_101B4100 path)
							ironFov92 = *(DWORD*)(wpn + 1192);   // iron FOV the gameplay path (sub_101B4060) blends TOWARD
								// [PROPS] one-shot structured dump of the weapon's RANGED property list (PropertyDataBasePC::
								// PropertyList @ weapon+0x28: count @ +8, 24-byte record array base @ +0xC; record: propID @ +0xC,
								// value @ +20). Finds WCPT_Scope_IronSightOffsetY_F by ID (FOV=90.0 anchors the ID convention).
								{
									static int g_props_n = 0;
									DWORD pcount = *(DWORD*)(wpn + 0x28 + 8);
									DWORD pbase  = *(DWORD*)(wpn + 0x28 + 0xC);
									if (g_props_n < 1 && pbase >= 0x00010000 && pbase < 0x7FFF0000 && pcount > 0 && pcount < 400)
									{
										g_props_n++;
										sprintf(buffer, "[PROPS] list@wpn+0x28 count=%d base=%08X (idx:propID:valueBits)\n", pcount, pbase);
										Log(buffer);
										for (DWORD pi = 0; pi < pcount; pi += 8)
										{
											char* pp = buffer;
											pp += sprintf(pp, "[PROPS]");
											for (DWORD pj = pi; pj < pi + 8 && pj < pcount; pj++)
											{
												DWORD rec = pbase + 24 * pj;
												pp += sprintf(pp, " %d:%03X:%08X", pj, *(DWORD*)(rec + 0xC), *(DWORD*)(rec + 20));
											}
											sprintf(pp, "\n");
											Log(buffer);
										}
									}
								}
							// [FIFOV] zoom-via-data test: set the weapon's iron-FOV blend TARGET to 50deg so the EXISTING
							// blend (sub_101B4060) zooms ADS smoothly. SAFE (sets source data, not a mid-pipeline value).
							if (g_forceironfov) *(float*)(wpn + 1192) = 50.0f;
							DWORD ironH = *(DWORD*)(wpn + 912);
							if (ironH)
							{
								int jbuf[20];
								((int*(__cdecl*)(int*, int, unsigned char))(baseAddressAI + 0xD96C0))(jbuf, (int)ironH, *(unsigned char*)(wpn + 916));
								gun[0] = ((float*)jbuf)[4]; gun[1] = ((float*)jbuf)[5]; gun[2] = ((float*)jbuf)[6];
								gunOK = 1;
							}
						}
						sprintf(buffer, "[GEOM] cam=(%.3f,%.3f,%.3f) feet=(%.3f,%.3f,%.3f) headY=(%.3f,%.3f,%.3f) gunOK=%d gunIron=(%.3f,%.3f,%.3f) ironFOV84=0x%08X ironFOV92=0x%08X\n",
							out[0], out[1], out[2], feet[0], feet[1], feet[2], hY[0], hY[1], hY[2], gunOK, gun[0], gun[1], gun[2], ironFov84, ironFov92);
						Log(buffer);
					}
				}
				_fxrstor(g_mo_fxbuf);
			}
		}
	}
	return r;
}

// [EYEBACK3] THE render-camera push. The 2.0m tests on cam+472 / camera[11]+0x44 were pixel-identical -> those are the
// GAMEPLAY camera (what vGetPos queries). The RENDERER reads a Yeti viewport node set via:
//   AI_Camera::Update -> sub_101B2560 -> sub_101B1930(this, a2) -> qmemcpy(this+20,a2,0x40); OBJ_SetMatrix(this[2], a2)
// a2 is the final 4x4 row-major camera matrix; its translation is at +0x30 = M[12]/M[13]/M[14]. We hook sub_101B1930
// and pull that translation BACK along the aim heading (scaled by ironF) BEFORE the original caches+pushes it, so the
// viewport node receives the pulled-back eye -> the GPU renders it. this = cam+372, so camera = this-372 and we gate to
// the LOCAL player camera via camera+12 == playerAddress (the followed AI_EntityPlayer), while aiming (ironF in (0.01,1]).
const char* (__fastcall* org_RenderMatrixPush_diag)(void*, void*, void*) = 0;
static int g_eb3_n = 0;
const char* __fastcall RenderMatrixPush_diag(void* THIS, void* EDX, void* a2)
{
	// Gate on the LIVE iron factor (g_lastIronF, fed by IronFactor_diag this same frame) + playerAddress -- NOT on THIS's
	// layout (THIS is a node-binding object, not the AI_Camera; cam+12/ectx were bogus). a2 IS the render matrix, eye at
	// row-major M[12]/M[13]/M[14] (confirmed: tRow matched the [GEOM] eye, tCol was zero).
	if (a2 && playerAddress && g_eyeback != 0.0f && g_lastIronF > 0.01f && g_lastIronF <= 1.0f)
	{
		DWORD pgao = *(DWORD*)(playerAddress + 0x14);
		if (pgao >= 0x00010000 && pgao < 0x7FFF0000 && (pgao & 3) == 0)
		{
			_fxsave(g_mo_fxbuf);
			float hY[3] = { 0, 0, 0 };
			((float*(__cdecl*)(float*, int))(baseAddressAI + 0x72D90))(hY, (int)pgao);  // OBJ_vGetYaxis = aim heading
			float* M = (float*)a2;
			float bx = M[12], by = M[13], bz = M[14];
			float s = g_eyeback * g_lastIronF;          // smooth ramp: 0 at hip -> g_eyeback m at full ADS
			M[12] = bx - hY[0] * s;
			M[13] = by - hY[1] * s;
			M[14] = bz - hY[2] * s;
			if (g_eb3_n < 8)
			{
				g_eb3_n++;
				sprintf(buffer, "[EYEBACK3] applied ironF=%.3f s=%.3f eye (%.3f,%.3f,%.3f)->(%.3f,%.3f,%.3f) headY=(%.3f,%.3f,%.3f)\n",
					g_lastIronF, s, bx, by, bz, M[12], M[13], M[14], hY[0], hY[1], hY[2]);
				Log(buffer);
			}
			_fxrstor(g_mo_fxbuf);
		}
	}
	return org_RenderMatrixPush_diag(THIS, EDX, a2);   // original caches (this+20) + OBJ_SetMatrix(this[2], a2) the modified matrix
}

// [EYEBACK4] hook AIDLL::OBJ_SetMatrix(node, matrix) @0x10061FC0 -- the GAO->engine matrix bridge. Gate to the RENDER
// camera GAO (g_cameraGAO = AI_CameraBase[5], the viewport-bound camera) so we catch the EXACT write the renderer
// reads, wherever it is issued. sub_101B1930's OBJ_SetMatrix targeted a DIFFERENT node (gameplay/HUD camera -> no
// visual change despite a correct 2m edit). If [EYEBACK4] lines appear, the render camera GAO IS set via OBJ_SetMatrix
// and we pull its translation back here; if none appear, the viewport camera is written by another path (engine-side).
const char* (__cdecl* org_SetMatrix_diag)(int, void*) = 0;
static int g_sm_n = 0;
const char* __cdecl SetMatrix_diag(int node, void* matrix)
{
	if (matrix && g_cameraGAO && node == (int)g_cameraGAO)
	{
		float* M = (float*)matrix;
		// confirm the render camera GAO IS set via OBJ_SetMatrix (capped log)
		if (g_sm_n < 12)
		{
			_fxsave(g_mo_fxbuf);
			g_sm_n++;
			sprintf(buffer, "[EYEBACK4] cameraGAO=%08X SetMatrix ironF=%.3f trans=(%.3f,%.3f,%.3f)\n",
				node, g_lastIronF, M[12], M[13], M[14]);
			Log(buffer);
			_fxrstor(g_mo_fxbuf);
		}
		// pull the render camera translation back along the aim heading while aiming
		if (playerAddress && g_eyeback != 0.0f && g_lastIronF > 0.01f && g_lastIronF <= 1.0f)
		{
			DWORD pgao = *(DWORD*)(playerAddress + 0x14);
			if (pgao >= 0x00010000 && pgao < 0x7FFF0000 && (pgao & 3) == 0)
			{
				_fxsave(g_mo_fxbuf);
				float hY[3] = { 0, 0, 0 };
				((float*(__cdecl*)(float*, int))(baseAddressAI + 0x72D90))(hY, (int)pgao);  // OBJ_vGetYaxis = aim heading
				float s = g_eyeback * g_lastIronF;
				M[12] -= hY[0] * s; M[13] -= hY[1] * s; M[14] -= hY[2] * s;
				_fxrstor(g_mo_fxbuf);
			}
		}
	}
	return org_SetMatrix_diag(node, matrix);
}

// [FOV] iron-sight FOV diag (installed with "_moodorder_"): hook sub_101B4100 @0x101B4100 -- the camera FOV loader.
// It sets the camera FOV (a3+112): if the stance-table entry's +402 flag is set AND the weapon exists, FOV =
// weapon iron FOV (weapon+1184 = WCPT_IronSight_FOV); else the per-stance table FOV (clamped). Logs, on change for
// the LOCAL pawn (THIS == playerAddress = the AI_EntityPlayer): the flag (is the weapon-FOV path taken?), the
// APPLIED FOV bits (a3+112), and the weapon's carried iron FOV bits (weapon+1184). Splits "ADS not zooming" into
// weapon-FOV-wrong/0 vs flag-not-set vs applied-but-==-hip. Raw float bits; fxsave/fxrstor-bracketed; integer log.
void (__fastcall* org_ApplyFov_diag)(void*, void*, int, int) = 0;
__declspec(align(16)) static unsigned char g_fov_fxbuf[512];
static int   g_fov_init = 0;
static int   g_fov_pFlag = -1;
static DWORD g_fov_pApplied = 0xFFFFFFFF;
static DWORD g_fov_pWpn = 0xFFFFFFFF;
void __fastcall ApplyFov_diag(void* THIS, void* EDX, int a2, int a3)
{
	org_ApplyFov_diag(THIS, EDX, a2, a3);          // run original first (sets a3+112 = FOV)
	// sub_101B4100's THIS is the CAMERA object (touches m_ShootPosition/vec220), NOT the pawn; the camera's
	// entity ref (the pawn) is at *(camera+12) (= sub_101B4100's this[3]). Gate to the LOCAL player's camera.
	if (!playerAddress) return;
	DWORD camPawn = *(DWORD*)((DWORD)THIS + 12);
	if (camPawn != playerAddress) return;
	// [FFOV] zoom test: once the iron blend is engaged (AI_CameraBase ironSightFactor @ camera[11]+212 > ~0.7),
	// force the FOV to 50 deg to confirm whether "looking over the gun" is just a missing ADS zoom. Integer-only
	// (raw float bits: 0x3F333333 = 0.7, 0x42480000 = 50.0). a3+112 is the game's FOV field -> persists after return.
	if (g_forcefov)
	{
		DWORD ffctx = *(DWORD*)((DWORD)THIS + 44);   // camera[11] = AI_CameraBase context
		if (ffctx >= 0x00010000 && ffctx < 0x7FFF0000 && (ffctx & 3) == 0 && *(DWORD*)(ffctx + 212) > 0x3F333333)
			*(DWORD*)(a3 + 112) = 0x42480000;        // FOV = 50.0 deg
	}
	_fxsave(g_fov_fxbuf);
	int   flag    = *(unsigned char*)(a2 + 402);   // table entry +402: use-weapon-FOV flag (iron stance)
	DWORD applied = *(DWORD*)(a3 + 112);           // the FOV that was set (raw bits)
	DWORD wpnFov  = 0xFFFFFFFF;
	DWORD wpn = ((DWORD(__fastcall*)(void*, void*))(baseAddressAI + 0x8BBF0))((void*)camPawn, 0); // GetWeaponComponent(pawn)
	if (wpn >= 0x00010000 && wpn < 0x7FFF0000 && (wpn & 3) == 0)
		wpnFov = *(DWORD*)(wpn + 1184);            // weapon iron FOV (WCPT_IronSight_FOV), raw bits
	if (!g_fov_init || flag != g_fov_pFlag || applied != g_fov_pApplied || wpnFov != g_fov_pWpn)
	{
		g_fov_init = 1; g_fov_pFlag = flag; g_fov_pApplied = applied; g_fov_pWpn = wpnFov;
		sprintf(buffer, "[FOV] useWeaponFov=%d(tbl+402) appliedFOV=0x%08X weaponIronFOV(+1184)=0x%08X\n", flag, applied, wpnFov);
		Log(buffer);
	}
	_fxrstor(g_fov_fxbuf);
}

// [SETFOV] FINAL gameplay FOV bridge diag (installed with "_moodorder_"): hook AIDLL::CAM_SetFieldOfView_0
// @0x10174AD0 (__cdecl(char viewport, float fov_RADIANS)) -- the ACTUAL FOV handed to the engine
// (ptr_BigStructure+460), fed by camera::CameraBase::SetFocal / sub_101B4060 (which BLENDS toward the weapon iron
// FOV weapon+1192 by the iron factor camera+212). THE definitive hip-vs-ADS zoom read: log viewport + FOV bits on
// change. Read-only (no forcing -> no crash). rad bits ref: 1.5708(=pi/2)=0x3FC90FDB(90deg); ~45deg=0x3F490FDB.
int (__cdecl* org_SetFov_diag)(char, float) = 0;
__declspec(align(16)) static unsigned char g_sf_fxbuf[512];
static int   g_sf_init = 0;
static int   g_sf_pVp = -999;
static DWORD g_sf_pFov = 0xFFFFFFFF;
int __cdecl SetFov_diag(char a1, float a2)
{
	int   vp  = (unsigned char)a1;
	DWORD fov = *(DWORD*)&a2;          // raw bits of the FOV (radians); no FP used
	if (!g_sf_init || vp != g_sf_pVp || fov != g_sf_pFov)
	{
		g_sf_init = 1; g_sf_pVp = vp; g_sf_pFov = fov;
		_fxsave(g_sf_fxbuf);
		sprintf(buffer, "[SETFOV] viewport=%d fovRadBits=0x%08X\n", vp, fov);
		Log(buffer);
		_fxrstor(g_sf_fxbuf);
	}
	return org_SetFov_diag(a1, a2);
}

// [ACS] CONSUMED stance-camera offset diag (installed with "_moodorder_"): hook AI_Camera_ApplyStanceCameraSettings
// @0x101B7900 (__fastcall, THIS=camera obj). The original indexes the CLIENT CameraSettings Zen table by the FINAL
// mode (THIS->m_ShootPosition.currValue.x @+0x204, AFTER the IronMode remap) and loads the over-shoulder/eye offset
// into THIS->vec220 @+0x220 (x/y/z). Hooking AFTER the original captures the consumed mode + the LOADED offset ->
// splits "wrong mode selected" (mode != the expected iron mode) vs "Zen table not loaded" (mode correct but offset
// 0/garbage). On-change only (the ADS transition changes the mode). FP-safe: raw integer reads; the %f formatting
// runs INSIDE the fxsave/fxrstor bracket which saves+restores the full FPU/SSE state.
int (__fastcall* org_ApplyStanceCam_diag)(void*, void*) = 0;
static int   g_acs_init = 0;
static int   g_acs_pMode = -999;
static DWORD g_acs_pX = 0, g_acs_pY = 0, g_acs_pZ = 0;
static unsigned char g_acs_entDumped[40] = {0};   // [ACSENT] one-time-per-mode raw table-entry dump guard
int __fastcall ApplyStanceCam_diag(void* THIS, void* EDX)
{
	int result = org_ApplyStanceCam_diag(THIS, EDX);   // run original first (loads vec220 from the Zen table)
	if (THIS)
	{
		char* p = (char*)THIS;
		int   mode = *(int*)(p + 0x204);                // m_ShootPosition.currValue.x = consumed mode index
		DWORD x = *(DWORD*)(p + 0x220);                 // vec220.x over-shoulder offset (raw bits)
		DWORD y = *(DWORD*)(p + 0x224);                 // vec220.y
		DWORD z = *(DWORD*)(p + 0x228);                 // vec220.z
		if (!g_acs_init || mode != g_acs_pMode || x != g_acs_pX || y != g_acs_pY || z != g_acs_pZ)
		{
			g_acs_init = 1; g_acs_pMode = mode; g_acs_pX = x; g_acs_pY = y; g_acs_pZ = z;
			_fxsave(g_mo_fxbuf);
			sprintf(buffer, "[ACS] consumedMode=%d overShoulder=(%.3f, %.3f, %.3f) bits=(%08X, %08X, %08X)\n\0",
				mode, *(float*)&x, *(float*)&y, *(float*)&z, x, y, z);
			Log(buffer);
			_fxrstor(g_mo_fxbuf);
		}
		// [ACSENT] one-time dump of the RAW CameraSettings Zen table entry for this mode.
		// entry = *(dword_1073DA7C + 88) + 416*mode  (dword_1073DA7C = the zen::CameraSettings singleton, sub_101FA620).
		// Full 416-byte (104-float) entry so mode 3 (hip-stand) vs mode 21 (iron-stand) can be diffed offline:
		// identical => the iron eye is NOT differentiated in the data; different => table fine, wrongness is downstream.
		if (mode >= 0 && mode < 40 && !g_acs_entDumped[mode])
		{
			DWORD sing = *(DWORD*)(baseAddressAI + 0x73DA7C);   // zen::CameraSettings singleton
			if (sing >= 0x00010000 && sing < 0x7FFF0000 && (sing & 3) == 0)
			{
				DWORD tbl = *(DWORD*)(sing + 88);               // table base
				if (tbl >= 0x00010000 && tbl < 0x7FFF0000 && (tbl & 3) == 0)
				{
					g_acs_entDumped[mode] = 1;
					float* e = (float*)(tbl + 416 * mode);
					_fxsave(g_mo_fxbuf);
					for (int i = 0; i < 104; i += 8)
					{
						sprintf(buffer, "[ACSENT] m%d e%03d: %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f\n",
							mode, i, e[i], e[i+1], e[i+2], e[i+3], e[i+4], e[i+5], e[i+6], e[i+7]);
						Log(buffer);
					}
					_fxrstor(g_mo_fxbuf);
				}
			}
		}
	}
	return result;
}

// ============================================================================
// INITENTITY-PROGRESS PINPOINT  (installed with "_deploydiag_")
// ----------------------------------------------------------------------------
// The native spawn crashes INSIDE AI_EntityPlayer::InitEntity @0x100D24A0 -- after the no-op UpdateMood
// (0x100d19fc) but before Order_ChangeMood(3) (0x100d249e); serializationFlags &1=1, NO assert, NO [VEL],
// NO [MO]. Hook the 4 internal calls (in InitEntity order) and log ENTER (before the original, FP-safe via
// fxsave/fxrstor so the st0 double arg is preserved) so the LAST [IE] line in the crash log is the call
// that crashed. Capped per-fn. RVAs off baseAddressAI: DeserializeRDVClassInfo 0x1C7A40, LoadWeapons
// 0xCEF50, LoadCharacterVisualsAsync 0x1CF450, AI_CameraBase::Init 0x1B1430.
__declspec(align(16)) static unsigned char g_ie_fxbuf[512];
static int g_ie_n1 = 0, g_ie_n2 = 0, g_ie_n3 = 0, g_ie_n4 = 0;
#define IE_LOG(ctr, nm, T) do { if ((ctr) < 16) { (ctr)++; _fxsave(g_ie_fxbuf); \
	sprintf(buffer, "[IE] " nm " #%d this=0x%08X\n\0", (ctr), (DWORD)(T)); Log(buffer); _fxrstor(g_ie_fxbuf); } } while (0)

// EARLY-InitEntity calls (post no-op, pre DeserRDV) -- the crash is in this window. Each logs the ENTITY
// arg (the pawn), not the manager `this`, so we can match it to the [MOOD] pawn. RegisterEntityPlayer is
// __thiscall(mgr, entPlayer); Intels(sub_10033440) is __thiscall(mgr, st0 double, entPlayer) -- st0 is
// preserved by IE_LOG's fxsave/fxrstor; Buff(sub_1001EEF0) is __thiscall(mgr, entPlayer).
void (__fastcall* org_IE_RegPlayer)(void*, void*, void*) = 0;
void __fastcall IE_RegPlayer(void* MGR, void* EDX, void* ent) {
	IE_LOG(g_ie_n1, "RegisterEntityPlayer", ent); org_IE_RegPlayer(MGR, EDX, ent);
}
void (__fastcall* org_IE_Intels)(void*, void*, void*) = 0;
void __fastcall IE_Intels(void* MGR, void* EDX, void* ent) {
	IE_LOG(g_ie_n2, "IntelsMgr(sub_10033440)", ent); org_IE_Intels(MGR, EDX, ent);
}
void (__fastcall* org_IE_Buff)(void*, void*, void*) = 0;
void __fastcall IE_Buff(void* MGR, void* EDX, void* ent) {
	IE_LOG(g_ie_n3, "BuffMgr(sub_1001EEF0)", ent); org_IE_Buff(MGR, EDX, ent);
}

// m_Mood DESERIALIZE PROBE (installed with "_deploydiag_"): RDC_U32::Read_NR @0x10077c20 is the wire-reader
// that LoadFrom calls to apply the create blob's m_Mood (this=the RDC = the cReplicatedFlag; m_Mood's is
// pawn+0x81C, currValue THIS+8 = pawn+0x824). Logs each call's RDC + the value written, to PROVE whether
// LoadFrom lands m_Mood=0xCE in pawn+0x824 (look for a [RDR] with val=0xCE -> then something resets it
// before InitEntity). Capped. Read-only (original runs first; log fxsave/fxrstor-bracketed).
unsigned int (__fastcall* org_ReadNR)(void*, void*, void*) = 0;
static int g_rdr_n = 0;
unsigned int __fastcall ReadNR_probe(void* THIS, void* EDX, void* memBuf) {
	unsigned int r = org_ReadNR(THIS, EDX, memBuf);
	if (g_rdr_n < 60) {
		g_rdr_n++;
		_fxsave(g_ie_fxbuf);
		sprintf(buffer, "[RDR] Read_NR #%d rdc=0x%08X val=0x%X pawn?=0x%08X\n\0",
			g_rdr_n, (DWORD)THIS, *(DWORD*)((char*)THIS + 8), (DWORD)THIS - 0x81C);
		Log(buffer);
		_fxrstor(g_ie_fxbuf);
	}
	return r;
}
