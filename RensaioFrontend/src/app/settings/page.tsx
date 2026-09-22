"use client";

import React from 'react';
import { SettingsManager } from "@/components/comp/settings-manager";

export default function SettingsPage() {
  return (
    <div className="space-y-8">
      <SettingsManager
        showHeader={true}
        showSaveButton={true}
        showFooterSaveButton={true}
        title="Settings"
        description="Configure your Rensaiō application settings"
      />
    </div>
  );
}
