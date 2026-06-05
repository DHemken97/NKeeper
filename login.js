const { app, BrowserWindow } = require('electron');
const path = require('path');

// Prevent the Wayland/Vulkan collision error on Linux desktop environments
app.disableHardwareAcceleration();

function createLoginWindow() {
  const win = new BrowserWindow({
    width: 1200,
    height: 800,
    resizable: true,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true
    }
  });

  // If in dev mode, point directly to the local Vite dev server port.
  // Otherwise, read compiled assets from the static production directory.
  if (process.env.NODE_ENV === 'development') {
    win.loadURL('http://localhost:5173');
    win.webContents.openDevTools();
  } else {
    win.loadFile(path.join(__dirname, 'dist/index.html'));
  }
}

app.whenReady().then(() => {
  createLoginWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createLoginWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
