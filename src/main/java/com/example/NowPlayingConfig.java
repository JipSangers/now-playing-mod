package com.example;

import me.shedaniel.autoconfig.ConfigData;
import me.shedaniel.autoconfig.annotation.Config;
import me.shedaniel.autoconfig.annotation.ConfigEntry;

@Config(name = "nowplaying")
public class NowPlayingConfig implements ConfigData {
        // Which side of the screen the HUD panel should stick to
    public enum Side {
        LEFT,
        RIGHT
    }

    @ConfigEntry.Gui.Tooltip
        // Default position for the HUD panel
    public Side sidePosition = Side.RIGHT;

    @ConfigEntry.Gui.Tooltip
    @ConfigEntry.BoundedDiscrete(min = 0, max = 100)
    public int yPosition = 10;

    @ConfigEntry.Gui.Tooltip
    @ConfigEntry.BoundedDiscrete(min = 0, max = 100)
    public int backgroundOpacity = 55;    // Toggle individual parts of the HUD panel



    @ConfigEntry.Gui.Tooltip
    public boolean showCoverArt = true;

    @ConfigEntry.Gui.Tooltip
    public boolean showMediaTitle = true;

    @ConfigEntry.Gui.Tooltip
    public boolean showArtistName = true;

    @ConfigEntry.Gui.Tooltip
    public boolean showTimeline = true;

    @ConfigEntry.Gui.Tooltip
    public boolean showPlayStatusIcon = true;
}