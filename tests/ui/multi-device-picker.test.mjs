import test from 'node:test';
import assert from 'node:assert/strict';
import {createMiniFixture} from './helpers/mini-dom-fixture.mjs';

test('blue mini-window styling is isolated to Mac multi-device mode', () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Mac', multiDeviceMode: true});
  assert.equal(fixture.root.dataset.macCandidateBlue, 'true');
  assert.equal(fixture.q('[data-preview-toggle]').getAttribute('aria-label'), '查看对方画面');
  fixture.state({platform: 'Mac', multiDeviceMode: false});
  assert.equal(fixture.root.dataset.macCandidateBlue, 'false');
  assert.equal(fixture.q('[data-preview-toggle]').getAttribute('aria-label'), '实时画面');
  fixture.state({platform: 'Windows', multiDeviceMode: true});
  assert.equal(fixture.root.dataset.macCandidateBlue, 'false');
  fixture.state({platform: 'Windows', multiDeviceMode: false});
  assert.equal(fixture.root.dataset.macCandidateBlue, 'false');
});

test('legacy two-device window does not show the device selector', () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Windows'});
  assert.equal(fixture.q('[data-device-chooser]').hidden, true);
});

test('Windows peer can browse the host share without presenting a local share setting', () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Windows', multiDeviceMode: true, sharedFolderIsRemote: true,
    localDeviceName: 'peer', selectedDevice: 'host', devices: [{name: 'host', label: '主控'}]});
  assert.match(fixture.q('[data-shared-notice]').textContent, /主控/);
  assert.equal(fixture.q('[data-local-share-setting]').hidden, true);
  fixture.state({platform: 'Windows', multiDeviceMode: true, sharedFolderIsRemote: false,
    localDeviceName: 'host', selectedDevice: 'peer', devices: [{name: 'peer', label: '被控'}]});
  assert.equal(fixture.q('[data-local-share-setting]').hidden, false);
});

test('Mac peer labels local Mac and approved Windows host correctly', () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Mac', multiDeviceMode: true, localDeviceName: 'mac',
    selectedDevice: 'host', activeComputer: 'mac', devices: [{name: 'host', label: '主控'}]});
  const buttons = fixture.root.querySelectorAll('[data-switch]');
  const windows = buttons.find(button => button.dataset.switch === 'Windows');
  const mac = buttons.find(button => button.dataset.switch === 'Mac');
  assert.equal(windows.querySelector('span').textContent, '切到 主控');
  assert.equal(mac.querySelector('span').textContent, '本机 · 使用中');
  fixture.state({platform: 'Mac', multiDeviceMode: true, localDeviceName: 'mac',
    selectedDevice: 'host', activeComputer: 'host', devices: [{name: 'host', label: '主控'}]});
  assert.equal(windows.querySelector('span').textContent, '主控 · 使用中');
  assert.equal(mac.querySelector('span').textContent, '切到 本机');
});

test('multi-device labels and selection come from approved device state', async () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Windows', multiDeviceMode: true, deviceVersion: 1, localDeviceName: 'host', selectedDevice: 'two', activeComputer: 'host',
    devices: [{name: 'one', label: '工作电脑'}, {name: 'two', label: 'Mac 电脑'}]});
  const select = fixture.q('[data-device-picker]');
  assert.equal(fixture.q('[data-device-chooser]').hidden, false);
  assert.equal(select.children.length, 2);
  assert.equal(select.children[1].textContent, 'Mac 电脑');
  assert.equal(select.value, 'two');
  assert.match(fixture.q('[data-live-title]').textContent, /Mac 电脑/);
  assert.equal(fixture.last('device.rendered').payload.name, 'two');
  assert.equal(fixture.last('device.rendered').payload.version, 1);
  fixture.reply(fixture.last('device.rendered'), {});
  select.value = 'one';
  await select.fire('change');
  assert.equal(fixture.last('device.select').payload.name, 'one');
  assert.equal(select.value, 'two'); // the label remains the confirmed recipient until the switch completes
  await fixture.q('[data-choose-files]').fire('click');
  assert.equal(fixture.last('files.choose'), undefined);
  fixture.reply(fixture.last('device.select'), {});
  await fixture.flush();
});
