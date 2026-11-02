import fs from 'node:fs';

const read = p => fs.readFileSync(p, 'utf8');
const required = (text, token, message) => {
  if (!text.includes(token)) throw new Error(`${message}: missing ${token}`);
};
const forbidden = (text, token, message) => {
  if (text.includes(token)) throw new Error(`${message}: still contains ${token}`);
};

const metadata = read('src/GameServerControl.Shared/GameProfileMetadata.cs');
const profiles = read('src/GameServerControl.Shared/GameProfiles.cs');
const files = read('src/GameServerControl.Agent/Config/ServerConfigFileService.cs');
const profileCheck = read('tools/GameServerControl.ProfileCheck/Program.cs');

required(metadata, 'GameConfigRoot', 'Shared profile metadata must describe config roots');
required(metadata, 'ConfigRootsFor', 'Shared profile metadata must expose config-root lookup');
required(files, 'GameProfileMetadata.ConfigRootsFor', 'Config discovery must consume shared profile metadata');
forbidden(files, 'switch (profile)', 'Config discovery must no longer hard-code each profile');
required(profiles, '"enshrouded"', 'Curated profiles must include Enshrouded');
required(profiles, '"2278520"', 'Enshrouded dedicated-server App ID must be 2278520');
required(profileCheck, 'Duplicate profile key', 'ProfileCheck must reject duplicate keys');
required(profileCheck, 'Duplicate Steam App ID', 'ProfileCheck must reject duplicate App IDs');
required(profileCheck, 'Invalid default port', 'ProfileCheck must reject invalid default ports');

console.log('Scalable game-profile contract OK.');
