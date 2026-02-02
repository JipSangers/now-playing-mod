package com.example;

import net.fabricmc.api.ModInitializer;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

public class NowPlaying implements ModInitializer {
	    // Mod id used in Fabric metadata and for log tagging
    public static final String MOD_ID = "nowplaying";

	public static final Logger LOGGER = LoggerFactory.getLogger(MOD_ID);

	@Override
	    public void onInitialize() {
        // Common initialization (runs on both client and server)

		LOGGER.info("Hello Fabric world!");
	}
}