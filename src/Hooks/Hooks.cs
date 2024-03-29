﻿using Expedition;
using ImprovedInput;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RWCustom;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace RebindEverything;

internal static partial class Hooks
{
    public static void ApplyInit()
    {
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        On.RainWorld.PostModsInit += RainWorld_PostModsInit;
    }


    public static bool IsInit { get; private set; } = false;

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        try
        {
            ModOptions.RegisterOI();
            
            if (IsInit) return;
            IsInit = true;

            ApplyHooks();

            var mod = ModManager.ActiveMods.FirstOrDefault(mod => mod.id == Plugin.MOD_ID);

            Plugin.MOD_NAME = mod.name;
            Plugin.VERSION = mod.version;
            Plugin.AUTHORS = mod.authors;


            BackSpear.Description = "The key held to make Hunter either put or retrieve a spear from their back.";
            BackSlug.Description = "The key held to put or retrieve a Slugcat from your back.";

            Craft.Description = "The key held to make Artificer or Gourmand craft the items they are holding.";
            ArtiJump.Description = "The key pressed to make Artificer double jump, only works mid-air.";
            ArtiParry.Description = "The key pressed to make Artificer parry, forces a down input.";
            MakeSpear.Description = "The key held to have Spearmaster make a new spear.";
            Ascend.Description = "The key pressed to toggle Saint's ascension mode.";
            AimAscend.Description = "The key held to move the Saint's ascension reticle around.";

            Grapple.Description = "Affects Saint's Tongue & Grapple Worms.";


            BackSpear.HideConflict = k => k == BackSlug;
            BackSlug.HideConflict = k => k == BackSpear;


            ArtiJump.HideConflict = k => k == ArtiParry;
            ArtiParry.HideConflict = k => k == ArtiJump;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(ex);
        }
        finally
        {
            orig(self);
        }
    }

    private static void RainWorld_PostModsInit(On.RainWorld.orig_PostModsInit orig, RainWorld self)
    {
        orig(self);

        BackSlug.HideConfig = !ModManager.MSC && !ModManager.JollyCoop;

        Craft.HideConfig = !ModManager.MSC;
        ArtiJump.HideConfig = !ModManager.MSC || MachineConnector.IsThisModActive("danizk0.rebindartificer");
        ArtiParry.HideConfig = !ModManager.MSC || MachineConnector.IsThisModActive("danizk0.rebindartificer");
        MakeSpear.HideConfig = !ModManager.MSC;
        Ascend.HideConfig = !ModManager.MSC;
        AimAscend.HideConfig = !ModManager.MSC;
    }


    public static void ApplyHooks()
    {
        On.Player.ctor += Player_ctor;

        On.Player.checkInput += Player_checkInput;

        On.Player.ClassMechanicsSaint += Player_ClassMechanicsSaint;
        On.Player.TongueUpdate += Player_TongueUpdate;

        On.TubeWorm.GrabbedByPlayer += TubeWorm_GrabbedByPlayer;


        IL.Player.GrabUpdate += Player_GrabUpdateIL;
        IL.Player.ClassMechanicsArtificer += Player_ClassMechanicsArtificerIL;

        IL.Player.SpearOnBack.Update += SpearOnBack_UpdateIL;
        IL.Player.SlugOnBack.Update += SlugOnBack_UpdateIL;

        IL.Player.Update += Player_Update;
    }



    private static ConditionalWeakTable<Player, PlayerModule> PlayerData = new();

    private static void Player_ctor(On.Player.orig_ctor orig, Player self, AbstractCreature abstractCreature, World world)
    {
        orig(self, abstractCreature, world);

        PlayerData.Add(self, new PlayerModule());
    }



    private static void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);

        // We can replicate the normally required inputs to make gameplay with the rebinds more legitimate
        if ((self.SlugCatClass == MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer || (ExpeditionGame.explosivejump && !self.isSlugpup)) && ModOptions.artiJumpInput.Value)
        {
            bool flag2 = self.eatMeat >= 20 || self.maulTimer >= 15;
            bool isParryOverride = IsArtiParryCustomInput(self) && self.input[0].y < 0;


            bool artiJumpInput = IsArtiJumpCustomInput(self) && self.IsPressed(ArtiJump) && self.canJump <= 0 && !flag2 && self.bodyMode == Player.BodyModeIndex.Default && self.gravity != 0.0f && !isParryOverride;

            if (artiJumpInput) 
                self.input[0].jmp = true;



            bool artiParryInput = IsArtiParryCustomInput(self) && self.IsPressed(ArtiParry) && !self.submerged && !flag2 && self.gravity > 0.0f;

            if (!artiJumpInput && artiParryInput)
                self.input[0].y = -1;

            if (artiParryInput)
                self.input[0].jmp = true;
        }
    }



    // Arti Jump & Parry
    // I realised later that IL Hooking wasn't necessary, oh well...
    private static void Player_ClassMechanicsArtificerIL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        #region Arti Jump

        ILLabel afterJumpInput = null!;
        ILLabel afterJump = null!;

        // Get after jump input checks
        c.GotoNext(MoveType.Before,
            x => x.MatchLdfld<Player>("bodyMode"),
            x => x.MatchLdsfld<Player.BodyModeIndex>("ZeroG"),
            x => x.Match(OpCodes.Call),
            x => x.MatchBrtrue(out afterJumpInput));

        // Get after jump block
        c.GotoNext(MoveType.Before,
            x => x.MatchLdcR4(0.1f),
            x => x.MatchBgtUn(out afterJump));

        c.GotoPrev(MoveType.Before,
            x => x.MatchLdloc(0),
            x => x.MatchBrfalse(out _),
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>("pyroJumpped"));


        c.Index++;
        c.Emit(OpCodes.Pop);
        c.Emit(OpCodes.Ldarg_0);

        c.EmitDelegate<Func<Player, bool>>((self) => ArtiJumpPressed(self));

        // Custom check branch
        c.Emit(OpCodes.Brtrue, afterJumpInput);

        // Branch after if check returns false
        c.Emit(OpCodes.Br, afterJump);
        c.Emit(OpCodes.Ldloc, 0);

        #endregion

        #region Arti Parry

        ILLabel afterParryInput = null!;
        ILLabel afterParry = null!;

        c.GotoNext(MoveType.Before,
            x => x.Match(OpCodes.Call),
            x => x.MatchBrfalse(out _),
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>("canJump"),
            x => x.MatchLdcI4(0),
            x => x.MatchBgt(out afterParryInput));

        c.GotoPrev(MoveType.Before,
            x => x.MatchLdloc(0),
            x => x.MatchBrfalse(out afterParry),
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>("submerged"));

        c.Index++;
        c.Emit(OpCodes.Pop);
        c.Emit(OpCodes.Ldarg_0);

        c.EmitDelegate<Func<Player, bool>>((self) => ArtiParryPressed(self));

        c.Emit(OpCodes.Brtrue, afterParryInput);
        c.Emit(OpCodes.Br, afterParry);

        c.Emit(OpCodes.Ldloc, 0);

        #endregion
    }



    // Spear Extraction, Back Spear, Back Slug, Craft
    private static void Player_GrabUpdateIL(ILContext il)
    {
        BackSpearSlugIL(il);
        MakeSpearIL(il);
        CraftIL(il);
    }



    // Back Spears & Slugs
    // Warning: Moderate pain
    private static void BackSpearSlugIL(ILContext il)
    {
        ILCursor c = new ILCursor(il);


        #region Disable Setting Increment To False

        ILLabel afterIncrementFalseSpear = null!;

        while (c.TryGotoNext(MoveType.Before,
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>(nameof(Player.spearOnBack)),
            x => x.MatchBrfalse(out afterIncrementFalseSpear),
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>(nameof(Player.spearOnBack)),
            x => x.MatchLdcI4(0),
            x => x.MatchStfld<Player.SpearOnBack>(nameof(Player.SpearOnBack.increment))))   
        {
            c.Index++;
            c.Emit(OpCodes.Pop);

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate(IsBackSpearCustomInput);
            c.Emit(OpCodes.Brtrue, afterIncrementFalseSpear);

            c.Emit(OpCodes.Ldarg_0);
            
            c.Index++;
        }

        c.Index = 0;

        ILLabel afterIncrementFalseSlug = null!;

        while (c.TryGotoNext(MoveType.Before,
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>(nameof(Player.slugOnBack)),
            x => x.MatchBrfalse(out afterIncrementFalseSlug),
            x => x.MatchLdarg(0),
            x => x.MatchLdfld<Player>(nameof(Player.slugOnBack)),
            x => x.MatchLdcI4(0),
            x => x.MatchStfld<Player.SlugOnBack>(nameof(Player.SlugOnBack.increment))))
        {
            c.Index++;
            c.Emit(OpCodes.Pop);

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate(IsBackSlugCustomInput);
            c.Emit(OpCodes.Brtrue, afterIncrementFalseSlug);
            
            c.Emit(OpCodes.Ldarg_0);
            
            c.Index++; 
        }

        #endregion

        c.Index = 0;

        #region Custom Input Checks

        c.GotoNext(MoveType.Before,
            x => x.MatchLdcI4(-1),
            x => x.MatchStloc(7));


        // Slug To Back
        c.Emit(OpCodes.Ldloc, 7);
        c.Emit(OpCodes.Ldarg_0);

        c.EmitDelegate<Action<int, Player>>((grasps, self) =>
        {
            if (self.slugOnBack == null) return;
            

            if (!IsBackSlugCustomInput(self)) return;

            if (!PlayerData.TryGetValue(self, out var playerModule)) return;

            
            if (self.slugOnBack.HasASlug && !playerModule.HadASlug && BackSlugPressed(self))
                playerModule.CanTakeSlugOffBack = false;

            playerModule.HadASlug = self.slugOnBack.HasASlug;

            if (!BackSlugPressed(self))
                playerModule.CanTakeSlugOffBack = true;

            if (!playerModule.CanTakeSlugOffBack && self.slugOnBack.HasASlug) return;


            bool hasFreeHand = grasps > -1;
            bool holdingSlug = false;

            if (self.CanPutSlugToBack)
            {
                for (int n = 0; n < 2; n++)
                {
                    if (self.grasps[n] != null && self.grasps[n].grabbed is Player && !((Player)self.grasps[n].grabbed).dead)
                    {
                        holdingSlug = true;
                        break;
                    }
                }
            }

            if (self.spearOnBack != null && self.spearOnBack.HasASpear) return;

            if (hasFreeHand || self.CanRetrieveSlugFromBack || holdingSlug)
                self.slugOnBack.increment = BackSlugPressed(self);
        });



        // Spear To Back
        c.Emit(OpCodes.Ldloc, 5);
        c.Emit(OpCodes.Ldarg_0);

        c.EmitDelegate<Action<int, Player>>((grasps, self) =>
        {
            if (self.spearOnBack == null) return;


            if (!IsBackSpearCustomInput(self)) return;

            bool hasFreeHand = grasps > -1;
            bool holdingSpear = false;

            if (self.CanPutSpearToBack)
            {
                for (int m = 0; m < 2; m++)
                {
                    if (self.grasps[m] != null && self.grasps[m].grabbed is Spear)
                    {
                        holdingSpear = true;
                        break;
                    }
                }
            }

            if (hasFreeHand || self.CanRetrieveSpearFromBack || holdingSpear)
                self.spearOnBack.increment = BackSpearPressed(self);
        });

        #endregion


        #region Disable First Increment True Checks

        ILLabel afterSlugToBack = null!;

        c.GotoNext(MoveType.After,
            x => x.MatchCallOrCallvirt<Player>("get_CanRetrieveSlugFromBack"),
            x => x.MatchBrfalse(out afterSlugToBack));

        c.GotoPrev(MoveType.Before,
            x => x.MatchLdloc(7),
            x => x.MatchLdcI4(-1),
            x => x.MatchBgt(out _));

        c.Index++;
        c.Emit(OpCodes.Pop);

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate(IsBackSlugCustomInput);
        c.Emit(OpCodes.Brtrue, afterSlugToBack);
        
        c.Emit(OpCodes.Ldloc, 7);


        ILLabel afterSpearToBack = null!;

        c.GotoNext(MoveType.After,
            x => x.MatchCallOrCallvirt<Player>("get_CanRetrieveSpearFromBack"),
            x => x.MatchBrfalse(out afterSpearToBack));

        c.GotoPrev(MoveType.Before,
            x => x.MatchLdloc(5),
            x => x.MatchLdcI4(-1),
            x => x.MatchBgt(out _));

        c.Index++;
        c.Emit(OpCodes.Pop);

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate(IsBackSpearCustomInput);
        c.Emit(OpCodes.Brtrue, afterSpearToBack);

        c.Emit(OpCodes.Ldloc, 5);

        #endregion


        #region Disable Second Increment True Checks

        // Move Closer to target
        c.GotoNext(MoveType.After,
            x => x.MatchLdarg(0),
            x => x.MatchCallOrCallvirt<Creature>("get_grasps"),
            x => x.MatchLdloc(28));

        c.GotoNext(MoveType.After,
            x => x.MatchBgt(out _),
            x => x.MatchLdarg(0),
            x => x.MatchCallOrCallvirt<Player>("get_CanRetrieveSlugFromBack"));



        // Back Slugpup
        c.GotoPrev(MoveType.After,
            x => x.MatchLdfld<Player.InputPackage>(nameof(Player.InputPackage.pckp)));

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player, bool>>((player) => !IsBackSlugCustomInput(player));
        c.Emit(OpCodes.And);


        // Back Spear
        c.GotoNext(MoveType.After,
            x => x.MatchLdfld<Player.InputPackage>(nameof(Player.InputPackage.pckp)));

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player, bool>>((player) => !IsBackSpearCustomInput(player));
        c.Emit(OpCodes.And);

        #endregion
    }

    private static void SlugOnBack_UpdateIL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        c.GotoNext(MoveType.After,
            x => x.MatchLdfld<Player.InputPackage>(nameof(Player.InputPackage.pckp)));

        c.Emit(OpCodes.Pop);
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player.SlugOnBack, bool>>((self) => BackSlugPressed(self.owner));
    }

    private static void SpearOnBack_UpdateIL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        c.GotoNext(MoveType.After,
            x => x.MatchLdfld<Player.InputPackage>(nameof(Player.InputPackage.pckp)));

        c.Emit(OpCodes.Pop);
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player.SpearOnBack, bool>>((self) => BackSpearPressed(self.owner));
    }



    // TODO: Cleanup (nah lol)
    // Make Spear
    private static void MakeSpearIL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        ILLabel extractionDest = null!;
        ILLabel afterExtractionDest = null!;

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Action<Player>>((player) =>
        {
            PlayerData.TryGetValue(player, out var playerEx);
            if (playerEx == null) return;

            playerEx.WasMakeSpearInputRegistered = false;
        });



        // Retraction
        c.GotoNext(MoveType.Before,
            x => x.MatchLdarg(0),
            x => x.MatchCallOrCallvirt<Player>("get_input"),
            x => x.MatchLdcI4(0),
            x => x.MatchLdelema<Player.InputPackage>(),
            x => x.MatchLdfld<Player.InputPackage>("pckp"));

        c.RemoveRange(5);

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player, bool>>((self) =>
        {
            PlayerData.TryGetValue(self, out var playerEx);
            if (playerEx == null) return false;

            return MakeSpearPressed(self);
        });



        // Move closer to target
        c.GotoNext(MoveType.After,
            x => x.MatchCallOrCallvirt<Player>("PickupPressed"));


        // Get Destination
        c.GotoNext(MoveType.After,
            x => x.MatchLdloc(3),
            x => x.MatchLdcI4(-1),
            x => x.MatchBle(out extractionDest));


        // Extraction
        c.GotoNext(MoveType.After,
            x => x.MatchLdarg(0),
            x => x.MatchCallOrCallvirt<Player>("get_input"),
            x => x.MatchLdcI4(0),
            x => x.MatchLdelema<Player.InputPackage>(),
            x => x.MatchLdfld<Player.InputPackage>("y"),
            x => x.MatchBrtrue(out afterExtractionDest));

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player, bool>>((self) =>
        {
            PlayerData.TryGetValue(self, out var playerEx);
            if (playerEx == null) return true;

            playerEx.WasMakeSpearInputRegistered = true;

            return MakeSpearPressed(self);
        });

        c.Emit(OpCodes.Brfalse, afterExtractionDest);



        // Move just before PickupPressed checks
        c.GotoNext(MoveType.After,
            x => x.MatchStfld<Player>("wantToThrow"));

        c.Index++;
        c.Emit(OpCodes.Pop);

        // Branch back to check extraction
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player, bool>>((player) =>
        {
            PlayerData.TryGetValue(player, out var playerEx);
            if (playerEx == null) return false;

            bool wasInputAlreadyProcessed = playerEx.WasMakeSpearInputRegistered;
            playerEx.WasMakeSpearInputRegistered = true;

            return wasInputAlreadyProcessed;
        });

        c.Emit(OpCodes.Brfalse, extractionDest);
        c.Emit(OpCodes.Ldloc_S, (byte)6);
    }



    // Craft
    private static void CraftIL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        c.GotoNext(MoveType.Before,
            x => x.MatchLdcI4(-1),
            x => x.MatchStloc(7));


        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Action<Player>>((self) =>
        {
            if (!PlayerData.TryGetValue(self, out var playerModule)) return;

            playerModule.IsCrafting = false;


            if (!CraftPressed(self)) return;

            if (ModManager.MSC && CustomGraspsCanBeCrafted(self))
            {
                self.craftingObject = true;
                playerModule.IsCrafting = true;
                return;
            }
        });


        c.Emit(OpCodes.Ldloc_1);
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<bool, Player, bool>>((flag3, self) =>
        {
            if (!IsCraftCustomInput(self)) return flag3;

            if (!PlayerData.TryGetValue(self, out var playerModule)) return flag3;

            if (playerModule.IsCrafting) return true;

            return flag3;
        });
        c.Emit(OpCodes.Stloc_1);


        c.Emit(OpCodes.Ldloc, 6);
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<int, Player, int>>((num5, self) =>
        {
            if (!IsCraftCustomInput(self)) return num5;

            if (!PlayerData.TryGetValue(self, out var playerModule)) return num5;

            if (playerModule.IsCrafting) return -1;

            return num5;
        });
        c.Emit(OpCodes.Stloc, 6);


        c.GotoNext(MoveType.After,
            x => x.MatchBrfalse(out _),
            x => x.MatchLdarg(0),
            x => x.MatchCallOrCallvirt<Player>(nameof(Player.GraspsCanBeCrafted)));

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<Player, bool>>((self) => !IsCraftCustomInput(self));
        c.Emit(OpCodes.And);
    }

    private static bool CustomGraspsCanBeCrafted(Player self)
    {
        bool artiCraft = self.SlugCatClass == MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer && self.CraftingResults() != null;
        bool gourmandCraft = self.SlugCatClass == MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Gourmand && self.CraftingResults() != null;
        
        if (!artiCraft && !gourmandCraft)
        {
            return ModManager.Expedition && Custom.rainWorld.ExpeditionMode && ExpeditionGame.activeUnlocks.Contains("unl-crafting") && self.CraftingResults() != null;
        }

        return true;
    }



    // Ascend, Aim Ascend & Grapple
    private static void Player_ClassMechanicsSaint(On.Player.orig_ClassMechanicsSaint orig, Player self)
    {
        if (!PlayerData.TryGetValue(self, out var playerModule))
        {
            orig(self);
            return;
        }


        int wasWantToJump = self.wantToJump;
        bool wasPckpInput = self.input[0].pckp;
        bool wasThrwInput = self.input[0].thrw;
        bool wasJmpInput = self.input[0].jmp;

        bool wasAscension = self.monkAscension;

        if (IsAscendCustomInput(self))
        {
            bool ascensionInput = self.JustPressed(Ascend);

            self.wantToJump = ascensionInput ? 1 : 0;
            self.input[0].pckp = ascensionInput && !self.monkAscension;
        }

        if (IsAimAscendCustomInput(self))
        {
            bool moveAscensionInput = AimAscendPressed(self);

            if (self.monkAscension)
                self.input[0].thrw = moveAscensionInput;
        }

        if (IsGrappleCustomInput(self))
            self.input[0].jmp = self.JustPressed(Grapple);



        orig(self);


        if (wasAscension == self.monkAscension)
            self.wantToJump = wasWantToJump;

        self.input[0].pckp = wasPckpInput;
        self.input[0].thrw = wasThrwInput;
        self.input[0].jmp = wasJmpInput;
    }



    // Grapple
    private static void Player_TongueUpdate(On.Player.orig_TongueUpdate orig, Player self)
    {
        if (!PlayerData.TryGetValue(self, out var playerModule))
        {
            orig(self);
            return;
        }


        bool wasJmpInput = self.input[0].jmp;
        bool wasJmpInputLastFrame = self.input[1].jmp;



        if (IsGrappleCustomInput(self))
        {
            bool grappleInput = self.JustPressed(Grapple);

            self.input[0].jmp = grappleInput;
            self.input[1].jmp = false;
        }



        orig(self);



        self.input[0].jmp = wasJmpInput;
        self.input[1].jmp = wasJmpInputLastFrame;
    }

    private static void TubeWorm_GrabbedByPlayer(On.TubeWorm.orig_GrabbedByPlayer orig, TubeWorm self)
    {
        Player? player = null;
        for (int i = 0; i < self.grabbedBy.Count; i++)
        {
            if (self.grabbedBy[i].grabber is Player)
            {
                player = self.grabbedBy[i].grabber as Player;
                break;
            }
        }

        if (player == null)
        {
            orig(self);
            return;
        }



        bool wasJmpInput = player.input[0].jmp;



        if (IsGrappleCustomInput(player))
        {
            bool grappleInput = GrapplePressed(player);

            player.input[0].jmp = grappleInput;
        }



        orig(self);

        player.input[0].jmp = wasJmpInput;
    }


    // grapple worms i hate you
    private static void Player_Update(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        c.GotoNext(MoveType.After,
            x => x.MatchCallOrCallvirt<Room>(nameof(Room.PlaySound)),
            x => x.MatchPop(),
            x => x.MatchLdarg(0),
            x => x.MatchCallOrCallvirt<Player>("get_input"));

        c.GotoNext(MoveType.After,
            x => x.MatchLdloc(1));


        c.Emit(OpCodes.Ldarg_0);

        c.EmitDelegate<Func<bool, Player, bool>>((input, self) =>
        {
            if (!IsGrappleCustomInput(self))
                return input;

            return self.JustPressed(Grapple) && self.canJump < 1;
        });
    }



    private static int PlayerGraspsHas(this Player self, AbstractPhysicalObject.AbstractObjectType type)
    {
        for (int i = 0; i < self.grasps.Length; i++)
        {
            Creature.Grasp? grasp = self.grasps[i];
            if (grasp == null) continue;

            if (grasp.grabbed.abstractPhysicalObject.type == type)
                return i;
        }

        return -1;
    }

    private static int PlayerGraspsHas(this Player self, CreatureTemplate.Type type)
    {
        for (int i = 0; i < self.grasps.Length; i++)
        {
            Creature.Grasp? grasp = self.grasps[i];
            if (grasp == null) continue;

            if (grasp.grabbed is not Creature creature) continue;

            if (creature.Template.type == type)
                return i;
        }

        return -1;
    }
}
