# Teams App Package Instructions - UPDATED

## ? Issues Fixed in Manifest

The following validation errors have been resolved:
- ? **packageName**: Removed (not allowed in this schema version)
- ? **meetingExtensionDefinition**: Replaced with proper tabs configuration
- ? **Invalid scene properties**: Updated to use standard tab properties
- ? **Privacy/Terms pages**: Created required pages

## Required Files for Teams App Package

Your Teams app package needs these files in a ZIP file:

### 1. manifest.json ?
**UPDATED** - Now uses proper Teams app schema with:
- `configurableTabs` for meeting side panel integration
- `staticTabs` for personal app access
- Removed invalid `meetingExtensionDefinition` properties

### 2. Icons (Required)
You still need to create two PNG icon files:

#### icon-color.png
- **Size**: 192x192 pixels
- **Format**: PNG
- **Purpose**: Full-color icon shown in Teams

#### icon-outline.png  
- **Size**: 32x32 pixels
- **Format**: PNG (transparent background)
- **Purpose**: Outline icon for Teams UI

## Creating the App Package

1. **Create icons** using: `src/TeamsMeetingAssistant.Web/manifest/icon-generator.html`
2. **Create a folder** with these three files:
   - `manifest.json` (? **UPDATED** - copy the corrected version)
   - `icon-color.png` (192x192)
   - `icon-outline.png` (32x32)
3. **ZIP the folder contents** (select all 3 files, not the folder)
4. **Name it**: `TeamsApp-MeetingAssistant.zip`

## How the App Will Work

### Meeting Integration
- **Meeting Side Panel**: When added to a meeting, opens your app in the side panel
- **Personal Tab**: Also available as a personal app for testing
- **Real-time Updates**: Connects to your API for live transcript analysis

### App Permissions
- **identity**: Allows the app to access user information
- **messageTeamMembers**: Enables interaction with meeting participants

## Deployment Steps

### Step 1: Upload to Teams Admin Center
1. Go to **Teams Admin Center** (admin.teams.microsoft.com)
2. Navigate to **Teams apps** > **Manage apps**
3. Click **Upload new app**
4. Upload your `TeamsApp-MeetingAssistant.zip`
5. Set status to **Allowed**

### Step 2: Add to Meeting
1. **Join a Teams meeting**
2. **Click the Apps button** (+ icon in meeting toolbar)
3. **Search for "Meeting Assistant"**
4. **Click "Add"** ? Opens in side panel
5. **Enter meeting details** and start monitoring

## Testing the Complete Flow

Once deployed:
1. ? **App appears** in Teams meeting apps
2. ? **Side panel opens** with your Blazor interface  
3. ? **API connection** established through dev tunnels
4. ? **Real transcript data** flows from Teams to your app
5. ? **AI questions** generated and displayed in real-time

## Configuration Requirements

### API Settings
Make sure your API is configured for real Teams integration:
```json
{
  "UseMockServices": false,  // Use real Graph API
  "AllowedOrigins": "https://lv3nz16d-7251.use.devtunnels.ms"
}
```

### Graph API Permissions
Your Azure app registration needs these permissions:
- `OnlineMeetings.Read.All`
- `OnlineMeetingTranscript.Read.All`
- `User.Read`

The corrected manifest should now upload successfully to Teams! ??