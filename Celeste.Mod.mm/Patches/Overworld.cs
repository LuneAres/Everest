#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it

using Celeste.Mod;
using Celeste.Mod.Meta;
using Celeste.Mod.UI;
using Monocle;
using System;
using System.Collections.Generic;
using MonoMod;
using Microsoft.Xna.Framework;

namespace Celeste {
    class patch_Overworld : Overworld {

        private float inputEase;

        private float sessionDetailsInputEase; // Added

        private bool transitioning;

        private class patch_InputEntity : Entity {


            // "exposing" fields for the patch
            public patch_Overworld Overworld;
            private Wiggler confirmWiggle;
            private Wiggler cancelWiggle;
            private float confirmWiggleDelay;
            private float cancelWiggleDelay;

            // Added fields
            private Wiggler sessionDetailsWiggle;
            private float sessionDetailsWiggleDelay;

            public extern void orig_ctor(Overworld overworld);

            [MonoModConstructor]
            public void ctor(Overworld overworld) {
                orig_ctor(overworld);
                sessionDetailsWiggle = Wiggler.Create(0.4f, 4f);
                Add(sessionDetailsWiggle);
            }

            public patch_InputEntity()
            : base() {
                // no-op. MonoMod ignores this - we only need this to make the compiler shut up.
            }

            public extern void orig_Update();
            public override void Update() {
                if (Input.MenuJournal.Pressed && sessionDetailsWiggleDelay <= 0f) {
                    sessionDetailsWiggle.Start();
                    sessionDetailsWiggleDelay = 0.5f;
                }
                sessionDetailsWiggleDelay -= Engine.DeltaTime;
                orig_Update();
            }

            public extern void orig_Render();
            public override void Render() {
                orig_Render();

                if (Overworld.sessionDetailsInputEase > 0f) {
                    string sessionDetailsLabel = Dialog.Clean("FILESELECT_SESSIONDETAILS_TOGGLE");
                    float sessionDetailsWidth = ButtonUI.Width(sessionDetailsLabel, Input.MenuJournal);

                    Vector2 pos = new Vector2(1880f, 1024f) + new Vector2((40 + sessionDetailsWidth) * (1f - Ease.CubeOut(Overworld.sessionDetailsInputEase)), -48f);
                    ButtonUI.Render(pos, sessionDetailsLabel, Input.MenuJournal, 0.5f, 1f, sessionDetailsWiggle.Value * 0.05f);
                }
            }
        }

        private bool customizedChapterSelectMusic = false;

#pragma warning disable CS0649 // variable defined in vanilla
        private Snow3D Snow3D;
#pragma warning restore CS0649

        public patch_Overworld(OverworldLoader loader)
            : base(loader) {
            // no-op. MonoMod ignores this - we only need this to make the compiler shut up.
        }

        // Adding this method is required so that BeforeRenderHooks work properly.
        public override void BeforeRender() {
            foreach (Component component in Tracker.GetComponents<BeforeRenderHook>()) {
                BeforeRenderHook beforeRenderHook = (BeforeRenderHook) component;
                if (beforeRenderHook.Visible) {
                    beforeRenderHook.Callback();
                }
            }
            base.BeforeRender();
        }

        public extern void orig_Update();
        public override void Update() {
            lock (AssetReloadHelper.AreaReloadLock) {
                orig_Update();

                bool showSessionDetailsUI = (Current is OuiFileSelect)
                             && !(Current as OuiFileSelect).SlotSelected;
                if (Overlay == null && !transitioning || !showSessionDetailsUI) // TODO: why did I copy that, what does it mean?
                {
                    sessionDetailsInputEase = Calc.Approach(sessionDetailsInputEase, (showSessionDetailsUI && !Input.GuiInputController(Input.PrefixMode.Latest)) ? 1 : 0, Engine.DeltaTime * 4f);
                }

                // if the mountain model is currently fading, use the one currently displayed, not the one currently selected, which is different if the fade isn't done yet.
                patch_AreaData currentAreaData = null;
                string currentlyDisplayedSID = (Mountain?.Model as patch_MountainModel)?.PreviousSID;
                if (currentlyDisplayedSID != null) {
                    // use the settings of the currently displayed mountain
                    currentAreaData = patch_AreaData.Get(currentlyDisplayedSID);
                } else if (SaveData.Instance != null) {
                    // use the settings of the currently selected map
                    currentAreaData = patch_AreaData.Get(SaveData.Instance.LastArea);
                }
                MapMetaMountain mountainMetadata = currentAreaData?.Meta?.Mountain;

                Snow3D.Visible = mountainMetadata?.ShowSnow ?? true;

                if (string.IsNullOrEmpty(Audio.CurrentMusic)) {
                    // don't change music if no music is currently playing
                    return;
                }

                if (SaveData.Instance != null && (IsCurrent<OuiChapterSelect>() || IsCurrent<OuiChapterPanel>()
                    || IsCurrent<OuiMapList>() || IsCurrent<OuiMapSearch>() || IsCurrent<OuiJournal>())) {

                    string backgroundMusic = mountainMetadata?.BackgroundMusic;
                    string backgroundAmbience = mountainMetadata?.BackgroundAmbience;
                    if (backgroundMusic != null || backgroundAmbience != null) {
                        // current map has custom background music
                        Audio.SetMusic(backgroundMusic ?? "event:/music/menu/level_select");
                        Audio.SetAmbience(backgroundAmbience ?? "event:/env/amb/worldmap");
                        customizedChapterSelectMusic = true;
                    } else {
                        // current map has no custom background music
                        restoreNormalMusicIfCustomized();
                    }

                    foreach (KeyValuePair<string, float> musicParam in mountainMetadata?.BackgroundMusicParams ?? new Dictionary<string, float>()) {
                        Audio.SetMusicParam(musicParam.Key, musicParam.Value);
                    }
                } else {
                    // no save is loaded or we are not in chapter select
                    restoreNormalMusicIfCustomized();
                }
            }
        }

        public extern void orig_ReloadMountainStuff();
        public new void ReloadMountainStuff() {
            orig_ReloadMountainStuff();

            // reload all loaded custom mountain models as well.
            foreach (ObjModel customMountainModel in MTNExt.ObjModelCache.Values) {
                customMountainModel.ReassignVertices();
            }
        }

        public extern void orig_End();
        public override void End() {
            orig_End();

            if (!EnteringPico8) {
                Remove(Snow);
                ((patch_RendererList) (object) RendererList).UpdateLists();
                Snow = null;
            }
        }

        private void restoreNormalMusicIfCustomized() {
            if (customizedChapterSelectMusic) {
                SetNormalMusic();
                customizedChapterSelectMusic = false;
            }
        }
    }
}
