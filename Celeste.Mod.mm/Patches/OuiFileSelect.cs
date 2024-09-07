using Celeste.Mod.Core;
using Monocle;
using MonoMod;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static System.Formats.Asn1.AsnWriter;
using System.Dynamic;

namespace Celeste {
    class patch_OuiFileSelect : OuiFileSelect {

        public SessionDetailsPage SessionDetailsPage;
        public bool ShowingSessionDetails;


        public float SessionDetailsEase;
        /* Used to coordinate the animation
         * (also locks inputs for the first half of the animation (expect) for toggling session details, so you can revert your button press instantaneously)
         * 
         * |-----|-----|-----|-----|
         * 0     1     2     3     4
         * 
         * move slots:
	     *     0 to 2
	     *     -> calling OuiFileSelecSlot.MoveTo(...) when crossing '2' while decreasing or if already between 0 and 2
	     *     -> that launches a 0.25 second tween
         * change ticketRenderPosition.X:
	     *     1 to 2
         * change ticketRenderPosition.Y:
	     *     2 to 3
         * show session details:
	     *     2 to 4
	     *     -> calling SessionDetailsPage.Show() when crossing '2' while increasing or if already between 2 and 4
	     *     -> the timing is then done by SessionDetailsPage
	     */

        internal bool startingNewFile;

        [PatchOuiFileSelectSubmenuChecks] // we want to manipulate the orig method with MonoModRules
        public extern IEnumerator orig_Enter(Oui from);
        public new IEnumerator Enter(Oui from) {
            // ShowingSessionDetails = false; // TODO: check if that's needed

            if (SessionDetailsPage == null) {
                SessionDetailsPage = new SessionDetailsPage();
            }
            Scene.Add(SessionDetailsPage);

            if (!Loaded) {
                int maxSaveFile;

                if (CoreModule.Settings.MaxSaveSlots != null) {
                    maxSaveFile = Math.Max(3, CoreModule.Settings.MaxSaveSlots.Value);

                } else {
                    // first load: we want to check how many slots there are by checking which files exist in the Saves folder.
                    maxSaveFile = 1; // we're adding 2 later, so there will be at least 3 slots.
                    string saveFilePath = patch_UserIO.GetSaveFilePath();
                    if (Directory.Exists(saveFilePath)) {
                        foreach (string filePath in Directory.GetFiles(saveFilePath)) {
                            string fileName = Path.GetFileName(filePath);
                            // is the file named [number].celeste?
                            if (fileName.EndsWith(".celeste") && int.TryParse(fileName.Substring(0, fileName.Length - 8), out int fileIndex)) {
                                maxSaveFile = Math.Max(maxSaveFile, fileIndex);
                            }
                        }
                    }

                    // if 2.celeste exists, slot 3 is the last slot filled, therefore we want 4 slots (2 + 2) to always have the latest one empty.
                    maxSaveFile += 2;
                }

                Slots = new OuiFileSelectSlot[maxSaveFile];
            }

            int slotIndex = 0;
            IEnumerator orig = orig_Enter(from);
            while (orig.MoveNext()) {
                if (orig.Current is float f && f == 0.02f) {
                    // only apply the delay if the slot is on-screen (less than 2 slots away from the selected one).
                    if (Math.Abs(SlotIndex - slotIndex) <= 2) {
                        yield return orig.Current;
                    }
                    slotIndex++;
                } else {
                    yield return orig.Current;
                }
            }
        }

        [PatchOuiFileSelectSubmenuChecks] // we want to manipulate the orig method with MonoModRules
        public extern IEnumerator orig_Leave(Oui next);
        public new IEnumerator Leave(Oui next) {
            
            Scene.Remove(SessionDetailsPage);

            int slotIndex = 0;
            IEnumerator orig = orig_Leave(next);
            while (orig.MoveNext()) {
                if (orig.Current is float f && f == 0.02f) {
                    if (next is OuiFileNaming && SlotIndex == slotIndex) {
                        // vanilla moves the file slot at the Y position slot 0 is supposed to be.
                        // ... this doesn't work in our case, since slot 0 might be offscreen.
                        Slots[slotIndex].MoveTo(Slots[slotIndex].IdlePosition.X, 230f);
                    }

                    // only apply the delay if the slot is on-screen (less than 2 slots away from the selected one).
                    if (Math.Abs(SlotIndex - slotIndex) <= 2) {
                        yield return orig.Current;
                    }
                    slotIndex++;
                } else {
                    yield return orig.Current;
                }
            }
        }

#pragma warning disable CS0626 // extern method with no attribute
        public extern void orig_Update();
#pragma warning restore CS0626
        public override void Update() {
            float easeBefore = SessionDetailsEase;
            SessionDetailsEase = Calc.Approach(SessionDetailsEase, (ShowingSessionDetails) ? 1 : 0, Engine.DeltaTime * 2f); // takes 0.5s (twice as much as usual movements)

            // That's just to time the sesion details animation (moving the files) :
            if (!SlotSelected) {
                if (easeBefore < 0.5f && SessionDetailsEase >= 0.5f) {
                    // showing the details page if we're showing the session details and cross 0.5 easing
                    SessionDetailsPage.Show();
                } else if (easeBefore > 0.5f && SessionDetailsEase <= 0.5f) {
                    // move the slots if we're hiding the session details and cross 0.5 easing
                    for (int i = 0; i < Slots.Length; i++) {
                        Vector2 pos = Slots[i].IdlePosition;
                        Slots[i].MoveTo(pos.X, pos.Y);
                    }
                }
            }

            int initialFileIndex = SlotIndex; // used to catch if the selection moved

            if (ShowingSessionDetails) {
                // I don't wan't the orig_Update to catch the inputs
                // (this implementation works because Focused isn't changed by orig_Update)
                bool focused = Focused;
                Focused = false;
                orig_Update();
                Focused = focused;

                if (Focused && !SlotSelected) {
                    if (Input.MenuJournal.Pressed || Input.MenuCancel.Pressed) {
                        ShowingSessionDetails = false;
                        SessionDetailsPage.Hide();

                        if (SessionDetailsEase <= 0.5f) {
                            // If the ease value hase gone past this, then for a "nicer" animation we don't move the slots immediately. The movement is done higher in the Update method when the ease value passes this point
                            for (int i = 0; i < Slots.Length; i++) {
                                patch_OuiFileSelectSlot slot = (patch_OuiFileSelectSlot) Slots[i];
                                Vector2 pos = slot.IdlePosition;
                                slot.MoveTo(pos.X, pos.Y);
                            }
                        }
                    } else if (SessionDetailsEase > 0.5f) {
                        // we don't want the animation to snap, so we don't catch inputs other than for going back if SessionDetailsEase is too low (and I don't know how SlotSelected could be true, but hey...))
                        
                        if (Input.MenuUp.Pressed && SlotIndex > 0) {
                            Audio.Play("event:/ui/main/savefile_rollover_up");
                            SlotIndex--;
                            SessionDetailsPage.UpdateSessionInfos(((patch_SaveData) Slots[SlotIndex].SaveData), ((patch_SaveData) Slots[SlotIndex].SaveData)?.CurrentSession_Safe);
                            SessionDetailsPage.QuickShow();
                        } else if (Input.MenuDown.Pressed && SlotIndex < Slots.Length - 1) {
                            Audio.Play("event:/ui/main/savefile_rollover_down");
                            SlotIndex++;
                            SessionDetailsPage.UpdateSessionInfos(((patch_SaveData) Slots[SlotIndex].SaveData), ((patch_SaveData) Slots[SlotIndex].SaveData)?.CurrentSession_Safe);
                            SessionDetailsPage.QuickShow();
                        } else if (CoreModule.Settings.MenuPageUp.Pressed && SlotIndex > 0) {
                            Audio.Play("event:/ui/main/savefile_rollover_up");
                            float startY = Slots[SlotIndex].Y;
                            while (Slots[SlotIndex].Y > startY - 1080f && SlotIndex > 0) {
                                SlotIndex--;
                            }
                            SessionDetailsPage.UpdateSessionInfos(((patch_SaveData) Slots[SlotIndex].SaveData), ((patch_SaveData) Slots[SlotIndex].SaveData)?.CurrentSession_Safe);
                            SessionDetailsPage.QuickShow();
                        } else if (CoreModule.Settings.MenuPageDown.Pressed && SlotIndex < Slots.Length - 1) {
                            Audio.Play("event:/ui/main/savefile_rollover_down");
                            float startY = Slots[SlotIndex].Y;
                            while (Slots[SlotIndex].Y < startY + 1080f && SlotIndex < Slots.Length - 1) {
                                SlotIndex++;
                            }
                            SessionDetailsPage.UpdateSessionInfos(((patch_SaveData) Slots[SlotIndex].SaveData), ((patch_SaveData) Slots[SlotIndex].SaveData)?.CurrentSession_Safe);
                            SessionDetailsPage.QuickShow();
                        }
                    }
                }
            } else {
                // we don't want the animation to snap, so we don't catch the inputs if SessionDetailsEase is too high
                // (this implementation works because Focused isn't changed by orig_Update)
                bool focused = Focused;
                Focused = focused && (SessionDetailsEase < 0.5f);
                orig_Update();
                Focused = focused;

                if (Focused && !SlotSelected) {
                    if (Input.MenuJournal.Pressed) {
                        ShowingSessionDetails = true;
                        SessionDetailsPage.UpdateSessionInfos(((patch_SaveData) Slots[SlotIndex].SaveData), ((patch_SaveData) Slots[SlotIndex].SaveData)?.CurrentSession_Safe);

                        if (SessionDetailsEase >= 0.5f) {
                            // If the ease value isn't high enough we're not in the rigth part of the grand animation scheme to show the details page yet. It'll be done higher in the function when we pass this point
                            SessionDetailsPage.Show();
                        }

                        for (int i = 0; i < Slots.Length; i++) {
                            patch_OuiFileSelectSlot slot = (patch_OuiFileSelectSlot) Slots[i];
                            Vector2 pos = slot.IdlePosition;
                            slot.MoveTo(pos.X, pos.Y);
                        }
                    } else if (SessionDetailsEase < 0.5f) {
                        // again, we don't want the animation to snap, so we check SessionDetailsEase to lock inputs

                        if (CoreModule.Settings.MenuPageUp.Pressed && SlotIndex > 0) {
                            float startY = Slots[SlotIndex].Y;
                            while (Slots[SlotIndex].Y > startY - 1080f && SlotIndex > 0) {
                                SlotIndex--;
                            }
                            Audio.Play("event:/ui/main/savefile_rollover_up");
                        } else if (CoreModule.Settings.MenuPageDown.Pressed && SlotIndex < Slots.Length - 1) {
                            float startY = Slots[SlotIndex].Y;
                            while (Slots[SlotIndex].Y < startY + 1080f && SlotIndex < Slots.Length - 1) {
                                SlotIndex++;
                            }
                            Audio.Play("event:/ui/main/savefile_rollover_down");
                        }
                    }
                }
            }

            if (SlotIndex != initialFileIndex) {
                // selection moved, so update the Y position of all file slots.
                foreach (OuiFileSelectSlot slot in Slots) {
                    (slot as patch_OuiFileSelectSlot).ScrollTo(slot.IdlePosition.X, slot.IdlePosition.Y);
                }
            }
        }
    }

    public class SessionDetailsPage : Entity {

        private MTexture page = GFX.Gui["poempage"];
        private MTexture titleTexture;
        private MTexture accentTexture;
        private string areaName;
        private string chapter;
        private Color titleBaseColor;
        private Color titleAccentColor;
        private Color titleTextColor;
        private MTexture areaIcon;
        private MTexture tab = GFX.Gui["areaselect/tab"];
        private Color tabColor = Calc.HexToColor("3c6180");
        private MTexture modeIcon;
        private string modeString = ""; // An empty string will result in no mode shown (use a space to show only the modeIcon)
        private long time;
        private StrawberriesCounter strawberries;
        private DeathsCounter deaths; // I don't know why it's public in OuiFileSelectSlot
        private VirtualRenderTarget berryList;
        private const float maxBerryListHeight = 70f; // TODO: think about this value more carefully (that's a gut feeling one)
        private bool mapAlreadyCompleted;
        private bool sessionExists;
        private bool mapExists;

        private bool easingIn; // false if easing out (used to know if `ease` increases or decreases)
        private float ease; // 0.0 to 0.7: position     0.3 to 1.0: banner position     0.7 to 1.0: mode tab position

        private float positionEase {
            get {
                return Ease.CubeOut(Math.Clamp(10 * ease, 0, 7) / 7);
            }
        }

        private float bannerEaseOffset {
            get {
                return positionEase - Ease.CubeOut((Math.Clamp(10 * ease, 3, 10) - 3) / 7); // the "ease offset" with positionEase implied by a 0.1 difference in the original ease
            }
        }

        private float modeTabEase {
            get {
                return Ease.CubeInOut((Math.Clamp(10 * ease, 7, 10) - 7) / 3);
            }
        }

        public SessionDetailsPage() : base() {
            Active = true;
            Visible = false;
            easingIn = false;
            Collidable = false;
            Depth = -30; // to render it above the hovered slot when showing session details (and so, above the transparent black layer)
            Position = new Vector2(2220f, 400f);
            Add(deaths = new DeathsCounter(AreaMode.Normal, centeredX: true, 0));
            Add(strawberries = new StrawberriesCounter(true, 0));
            deaths.CanWiggle = false; // we will call deaths.Wiggle() manually on session infos update
            strawberries.CanWiggle = false; // it does an annoying sound when it wiggles because of a changing aomunt, but we will call strawberries.Wiggle() manually on session infos update

            AddTag(Tags.HUD); // Aaarg, why did I take so much time to figure it out?!
        }

        public override void Added(Scene scene) {
            base.Added(scene);
            berryList = VirtualContent.CreateRenderTarget("session-details_berry-list", (int) Math.Floor(page.Width * 0.8f), (int) Math.Floor(maxBerryListHeight));
        }

        public override void Removed(Scene scene) {
            base.Removed(scene);
            berryList.Dispose();
        }

        // This function allow to show details from any session, not only the current one from the savedata (even if we only use it like that, I want to have a more versatile interface here)
        //  -> TODO: this isn't really true yet (due to savedata.LevelSet)
        public void UpdateSessionInfos(patch_SaveData savedata, Session session) {

            if (savedata == null || session == null || !session.InArea) {
                sessionExists = false;
                deaths.Visible = false;
                strawberries.Visible = false;
                return;
            }

            sessionExists = true;

            // TODO: verify that this map availability check really works (taken from patch_LevelEnter.Routine)
            //   -> doesn't check if the mode exists (well, at the time of writing Everest doesn't do it either when loading the level from a session, so we don't care for now)
            mapExists = (AreaData.Get(session) != null);

            time = session.Time;
            strawberries.Amount = session.Strawberries.Count();
            strawberries.Visible = (strawberries.Amount > 0);
            strawberries.Wiggle();
            deaths.SetMode(session.Area.Mode);
            deaths.Amount = session.Deaths;
            deaths.Wiggle();
            deaths.Visible = true;

            switch (session.Area.Mode) {
                case AreaMode.BSide:
                    modeIcon = GFX.Gui["menu/remix"];
                    modeString = Dialog.Clean("overworld_remix");
                    break;
                case AreaMode.CSide:
                    modeIcon = GFX.Gui["menu/rmx2"];
                    modeString = Dialog.Clean("overworld_remix2");
                    break;
                default:
                    //modeIcon = GFX.Gui["menu/play"];
                    modeString = ""; // So the "mode" tab isn't displayed if on the A-Side
                    break;
            }

            mapAlreadyCompleted = session.OldStats.Modes[(int) session.Area.Mode].Completed;
            strawberries.ShowOutOf = mapAlreadyCompleted;

            if (mapExists) {
                patch_AreaData areadata = patch_AreaData.Get(session);

                bakeBerryListTexture(session, page.Width * 0.8f);
                strawberries.OutOf = session.MapData.ModeData.TotalStrawberries;

                titleBaseColor = areadata.TitleBaseColor;
                titleAccentColor = areadata.TitleAccentColor;
                titleTextColor = areadata.TitleTextColor;

                areaName = Dialog.Clean(areadata.Name);
                chapter = Dialog.Get("area_chapter").Replace("{x}", session.Area.ChapterIndex.ToString().PadLeft(2));

                string areaTextureName = $"areaselect/{areadata.Name}_title";
                string levelSetTextureName = $"areaselect/{savedata.LevelSet ?? "Celeste"}/title";
                if (GFX.Gui.Has(areaTextureName)) {
                    titleTexture = GFX.Gui[areaTextureName];
                } else if (GFX.Gui.Has(levelSetTextureName)) {
                    titleTexture = GFX.Gui[levelSetTextureName];
                } else {
                    titleTexture = GFX.Gui["areaselect/title"];
                }
                areaTextureName = $"areaselect/{areadata.Name}_accent";
                areaTextureName = $"areaselect/{savedata.LevelSet ?? "Celeste"}/accent";
                if (GFX.Gui.Has(areaTextureName)) {
                    accentTexture = GFX.Gui[areaTextureName];
                } else if (GFX.Gui.Has(levelSetTextureName)) {
                    accentTexture = GFX.Gui[levelSetTextureName];
                } else {
                    accentTexture = GFX.Gui["areaselect/accent"];
                }

                areaTextureName = $"areaselect/{areadata.Name}_accent";
                areaTextureName = $"areaselect/{savedata.LevelSet ?? "Celeste"}/accent";
                if (GFX.Gui.Has(areaTextureName)) {
                    accentTexture = GFX.Gui[areaTextureName];
                } else if (GFX.Gui.Has(levelSetTextureName)) {
                    accentTexture = GFX.Gui[levelSetTextureName];
                } else {
                    accentTexture = GFX.Gui["areaselect/accent"];
                }
                areaTextureName = $"areaselect/{areadata.Name}_accent";
                areaTextureName = $"areaselect/{savedata.LevelSet ?? "Celeste"}/accent";
                if (GFX.Gui.Has(areaTextureName)) {
                    accentTexture = GFX.Gui[areaTextureName];
                } else if (GFX.Gui.Has(levelSetTextureName)) {
                    accentTexture = GFX.Gui[levelSetTextureName];
                } else {
                    accentTexture = GFX.Gui["areaselect/accent"];
                }
                areaIcon = GFX.Gui[areadata.Icon];
            } else {
                titleBaseColor = Color.White;
                titleAccentColor = Color.Gray;
                titleTextColor = Color.Black;

                areaName = session.Area.GetSID(); // TODO: create a mod to cache map name dialogs?
                chapter = Dialog.Clean("FILESELECT_SESSIONDETAILS_MAPUNAVAILABLE");
                titleTexture = GFX.Gui["areaselect/title"];
                accentTexture = GFX.Gui["areaselect/accent"];
                areaIcon = GFX.Gui["areas/new"]; // TODO: create a mod to cache map icons?
            }
        }

        // mainly taken from GameplayStats
        private void bakeBerryListTexture(Session session, float width) {
            Engine.Graphics.GraphicsDevice.SetRenderTarget(berryList);
            Engine.Graphics.GraphicsDevice.Clear(Color.Transparent);
            Draw.SpriteBatch.Begin();

            ModeProperties mode = session.MapData.ModeData;

            int totalStrawberries = mode.TotalStrawberries;
            if (totalStrawberries <= 0) {
                Draw.SpriteBatch.End();
                return;
            }

            int spacing = 24;
            int numberOfLinesLeft = 10;
            float fullLinesOffset = 0f;
            float lastLineOffset = 0f;
            while ((1 + (numberOfLinesLeft - 1) * 1.5f) * spacing > maxBerryListHeight) { //
                spacing -= 2;

                int strawbWidth = (totalStrawberries - 1) * spacing;
                int checkpointWidth = ((totalStrawberries > 0 && mode.Checkpoints != null) ? (mode.Checkpoints.Length * spacing) : 0);

                numberOfLinesLeft = 0;

                fullLinesOffset = spacing / 2 + (width % spacing) / 2;
                lastLineOffset = (width - strawbWidth - checkpointWidth) / 2;
                while (lastLineOffset < spacing / 2) {
                    lastLineOffset += (width - spacing) / 2;
                    numberOfLinesLeft++;
                }
            }

            Vector2 pos;
            if (numberOfLinesLeft > 0) {
                pos = new Vector2(fullLinesOffset, spacing);
            } else {
                pos = new Vector2(lastLineOffset, spacing);
            }

            int checkpoints = ((mode.Checkpoints == null) ? 1 : (mode.Checkpoints.Length + 1));
            for (int c = 0; c < checkpoints; c++) {
                int checkpointTotal = ((c == 0) ? mode.StartStrawberries : mode.Checkpoints[c - 1].Strawberries);
                for (int i = 0; i < checkpointTotal; i++) {
                    EntityData atCheckpoint = mode.StrawberriesByCheckpoint[c, i];
                    if (atCheckpoint == null) {
                        continue;
                    }
                    bool currentHas = false;
                    foreach (EntityID strawb2 in session.Strawberries) {
                        if (atCheckpoint.ID == strawb2.ID && atCheckpoint.Level.Name == strawb2.Level) {
                            currentHas = true;
                        }
                    }
                    MTexture dot = GFX.Gui["dot"];
                    if (currentHas) {
                        if (session.Area.Mode == AreaMode.CSide) {
                            dot.DrawOutlineCentered(pos, Calc.HexToColor("f2ff30"), 1.5f * spacing / 32f);
                        } else {
                            dot.DrawOutlineCentered(pos, Calc.HexToColor("ff3040"), 1.5f * spacing / 32f);
                        }
                    } else {
                        bool oldHas = false;
                        foreach (EntityID strawb in session.OldStats.Modes[(int) session.Area.Mode].Strawberries) {
                            if (atCheckpoint.ID == strawb.ID && atCheckpoint.Level.Name == strawb.Level) {
                                oldHas = true;
                            }
                        }
                        if (oldHas) {
                            dot.DrawOutlineCentered(pos, Calc.HexToColor("4193ff"), spacing / 32f);
                        } else {
                            Draw.Rect(pos.X - (float) dot.ClipRect.Width * spacing / 32f * 0.5f, pos.Y - 4f, dot.ClipRect.Width * spacing / 32f, 5f, Color.Black * 0.4f);
                        }
                    }
                    pos.X += spacing;
                    if (pos.X > (width - spacing / 2)) {
                        numberOfLinesLeft--;
                        pos.Y += spacing * 1.5f;
                        if (numberOfLinesLeft > 0) {
                            pos.X = fullLinesOffset;
                        } else {
                            pos.X = lastLineOffset;
                        }
                    }
                }
                if (mode.Checkpoints != null && c < mode.Checkpoints.Length) {
                    Draw.Rect(pos.X - 3f * spacing / 32f, pos.Y - spacing / 2f, 6f * spacing / 32f, spacing, Color.Black * 0.4f);
                    pos.X += spacing;
                }
            }

            Draw.SpriteBatch.End();
        }

        public override void Update() {
            float previousEase = ease;
            ease = Calc.Approach(ease, (easingIn) ? 1 : 0, Engine.DeltaTime * 2f);

            if (previousEase > 0f && ease == 0f) {
                Visible = true; // maybe we should simplify this to `Visible = (ease == 0f)` and remove the Visible manips in the Show/Hide methods
            }

            Position = Vector2.Lerp(
                new Vector2(2220f, 400f),
                new Vector2(1000f, 400f),
                positionEase
            );

            base.Update();
        }

        public void Show() {
            easingIn = true;
            Visible = true;
        }

        public void QuickShow() {
            easingIn = true;
            ease = 0.7f; // just when the page reached it's position (so there is only the banner moving)
            Visible = true;
        }

        public void Hide() {
            easingIn = false;
            if (ease == 0f) {
                // Can't put Visible to false in most of the cases, since you want to see the hiding animation
                // Visible will be set to false in the Update method if the ease value goes from strictly positive to 0
                // here we handle the edge case of calling Hide when Visible is true and ease is 0, just in case (doing something like `detailsPage.Show(); detailsPage.Hide()` could cause that)
                Visible = false;
            }
        }

        public void InstantHide() {
            easingIn = false;
            ease = 0f;
            Visible = false;
        }

        public override void Render() {
            page.Draw(Position, Vector2.Zero, Color.White, new Vector2(1f, 0.85f));

            if (sessionExists) { // savedata shouldn't be null if session isn't (if the details have been updated)
                Vector2 bannerPosShift = 1220f * bannerEaseOffset * Vector2.UnitX;
                Vector2 tabPosShift = - tab.Height/2 * (1-modeTabEase) * Vector2.UnitY;

                if (modeString != "" && modeTabEase > 0f) {
                    Vector2 tabPos = Position + tabPosShift + new Vector2(760f, 218f);
                    tab.DrawCentered(tabPos, tabColor);
                    modeIcon.DrawCentered(tabPos + new Vector2(0f, -10f), Color.White, (float) (tab.Width - 50) / (float) modeIcon.Width);
                    ActiveFont.DrawOutline(modeString, tabPos + new Vector2(0f, -10 + modeIcon.Height * 0.3f), new Vector2(0.5f, 0f), Vector2.One * 0.7f, Color.White, 2f, Color.Black);
                }

                float x = Math.Max(
                    ActiveFont.Measure(areaName).X * ((mapExists) ? 1f : 0.8f),
                    ActiveFont.Measure(chapter).X * ((mapExists) ? 0.8f : 1f)
                );
                x = Math.Clamp(x - 570f, -20f, 80f);
                Vector2 titleBannerPos = Position + bannerPosShift + new Vector2(-x, 0);
                titleTexture.Draw(titleBannerPos, Vector2.Zero, titleBaseColor);
                accentTexture.Draw(titleBannerPos, Vector2.Zero, titleAccentColor);

                Vector2 areaIconPos = Position + bannerPosShift + new Vector2(790f, 86f);
                float scale = ((mapExists) ? 144f : 100f) / areaIcon.Width;
                areaIcon.DrawCentered(areaIconPos, Color.White, Vector2.One * scale);

                if (mapExists) {
                    ActiveFont.Draw(chapter, areaIconPos + new Vector2(-100f, -2f), new Vector2(1f, 1f), Vector2.One * 0.6f, titleAccentColor * 0.8f);
                    ActiveFont.Draw(areaName, areaIconPos + new Vector2(-100f, -18f), new Vector2(1f, 0f), Vector2.One, titleTextColor * 0.8f);
                } else {
                    ActiveFont.Draw(chapter, areaIconPos + new Vector2(-100f, 18f), new Vector2(1f, 1f), Vector2.One, titleTextColor * 0.8f);
                    ActiveFont.Draw(areaName, areaIconPos + new Vector2(-100f, 2f), new Vector2(1f, 0f), Vector2.One * 0.6f, titleAccentColor * 0.8f);
                }

                Vector2 linePos = new Vector2(page.Width / 2, 160f);

                string text = Dialog.Clean("FILESELECT_SESSIONDETAILS_ONGOINGSESSION");
                ActiveFont.Draw(text, Position + linePos, new Vector2(0.5f, 0f), Vector2.One, Color.Black * 0.8f);

                linePos.Y += ActiveFont.Measure(text).Y + 30;
                linePos.X = 230;

                if (strawberries.Amount > 0) {
                    deaths.Position = linePos;
                    linePos.Y += 80;
                    strawberries.Position = linePos;
                } else {
                    linePos.Y += 40;
                    deaths.Position = linePos;
                    linePos.Y += 40;
                }

                linePos.X = page.Width * 0.9f;
                linePos.Y += 20;

                ActiveFont.Draw(Dialog.Time(time), Position + linePos, new Vector2(1f, 0.5f), Vector2.One * 0.8f, Color.Black * 0.6f);

                linePos.Y = page.Height * 0.85f - 40 - maxBerryListHeight;

                if (mapAlreadyCompleted) {
                    if (mapExists) {
                        linePos.X = page.Width * 0.1f;
                        Draw.SpriteBatch.Draw((RenderTarget2D) berryList, Position + linePos, Color.White);
                    }
                } else {
                    linePos.X = page.Width / 2;
                    ActiveFont.Draw(Dialog.Clean("FILESELECT_SESSIONDETAILS_FIRSTPLAYTHROUGH"), Position + linePos, new Vector2(0.5f, 0f), Vector2.One * 0.8f, Color.Black * 0.4f);
                }
            } else {
                ActiveFont.Draw(Dialog.Clean("FILESELECT_SESSIONDETAILS_NOCURRENTSESSION"), Position + new Vector2(page.Width / 2, page.Height * 0.4f), new Vector2(0.5f, 0.5f), Vector2.One, Color.Black * 0.6f);
            }

            base.Render();
        }
    }
}

namespace MonoMod {
    /// <summary>
    /// Patches the checks for OuiAssistMode to include a check for OuiFileSelectSlot.ISubmenu as well.
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchOuiFileSelectSubmenuChecks))]
    class PatchOuiFileSelectSubmenuChecksAttribute : Attribute { }

    static partial class MonoModRules {

        public static void PatchOuiFileSelectSubmenuChecks(MethodDefinition method, CustomAttribute attrib) {
            TypeDefinition t_ISubmenu = method.Module.GetType("Celeste.OuiFileSelectSlot/ISubmenu");

            // The routine is stored in a compiler-generated method.
            method = method.GetEnumeratorMoveNext();

            bool found = false;

            ILProcessor il = method.Body.GetILProcessor();
            Mono.Collections.Generic.Collection<Instruction> instrs = method.Body.Instructions;
            for (int instri = 0; instri < instrs.Count - 4; instri++) {
                if (instrs[instri].OpCode == OpCodes.Brtrue_S
                    && instrs[instri + 1].OpCode == OpCodes.Ldarg_0
                    && instrs[instri + 2].OpCode == OpCodes.Ldfld
                    && instrs[instri + 3].MatchIsinst("Celeste.OuiAssistMode")) {
                    // gather some info
                    FieldReference field = (FieldReference) instrs[instri + 2].Operand;
                    Instruction branchTarget = (Instruction) instrs[instri].Operand;

                    // then inject another similar check for ISubmenu
                    instri++;
                    instrs.Insert(instri++, il.Create(OpCodes.Ldarg_0));
                    instrs.Insert(instri++, il.Create(OpCodes.Ldfld, field));
                    instrs.Insert(instri++, il.Create(OpCodes.Isinst, t_ISubmenu));
                    instrs.Insert(instri++, il.Create(OpCodes.Brtrue_S, branchTarget));

                    found = true;

                } else if (instrs[instri].OpCode == OpCodes.Ldarg_0
                    && instrs[instri + 1].OpCode == OpCodes.Ldfld
                    && instrs[instri + 2].MatchIsinst("Celeste.OuiAssistMode")
                    && instrs[instri + 3].OpCode == OpCodes.Brfalse_S) {
                    // gather some info
                    FieldReference field = (FieldReference) instrs[instri + 1].Operand;
                    Instruction branchTarget = instrs[instri + 4];

                    // then inject another similar check for ISubmenu
                    instri++;
                    instrs.Insert(instri++, il.Create(OpCodes.Ldfld, field));
                    instrs.Insert(instri++, il.Create(OpCodes.Isinst, t_ISubmenu));
                    instrs.Insert(instri++, il.Create(OpCodes.Brtrue_S, branchTarget));
                    instrs.Insert(instri++, il.Create(OpCodes.Ldarg_0));

                    found = true;
                }
            }


            if (!found) {
                throw new Exception("Call to isinst OuiAssistMode not found in " + method.FullName + "!");
            }
        }

    }
}
