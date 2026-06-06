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
	// m_Mood is a cReplicatedFlag (currValue is the bitmask). UpdateMood compares against the
	// 'prev mood' stored at this[1].m_bReplicatePositionAndAngle. We can't trivially resolve those
	// offsets from here without the full AI_EntityPlayer layout, so just log entry + return addr.
	void* caller = _ReturnAddress();
	sprintf(buffer, "[MOOD] UpdateMood ENTER this=0x%08X caller=0x%08X\n\0", (DWORD)THIS, (DWORD)caller);
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
