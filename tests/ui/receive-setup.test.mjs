import test from 'node:test';
import assert from 'node:assert/strict';
import {createMiniFixture} from './helpers/mini-dom-fixture.mjs';

test('pending incoming files on Mac offer a visible choice of receive folder', () => {
  const fixture=createMiniFixture();
  fixture.state({platform:'Mac',receiveDirectory:'',receiveSetupRequired:true});
  const notice=fixture.q('[data-receive-needed]');
  assert.ok(notice);
  assert.equal(notice.hidden,false);
  assert.match(notice.textContent,/选择.*收件/);
  fixture.q('[data-choose-receive]').onclick();
  assert.equal(fixture.last('directory.choose').payload.kind,'receive');
  fixture.state({platform:'Mac',receiveDirectory:'/synthetic/receive',receiveSetupRequired:false});
  assert.equal(notice.hidden,true);
});

test('an unconfigured receiver has an explicit folder action in the transfer panel', () => {
  for(const platform of ['Windows','Mac']) {
    const fixture=createMiniFixture();
    fixture.state({platform,receiveDirectory:'',receiveSetupRequired:false});
    const choose=fixture.q('[data-choose-receive-inline]');
    assert.equal(choose.hidden,false);
    choose.onclick();
    assert.equal(fixture.last('directory.choose').payload.kind,'receive');
    fixture.state({platform,receiveDirectory:'/synthetic/receive',receiveSetupRequired:false});
    assert.equal(choose.hidden,true);
  }
});

test('Windows queued sends explain both connectivity and Mac receive setup', () => {
  const fixture=createMiniFixture();
  fixture.state({platform:'Windows',transfers:[{id:'example',name:'sample.txt',state:'queued',bytesTransferred:0,totalBytes:4}]});
  assert.match(fixture.q('[data-transfers]').textContent,/等待 Mac 接收/);
  assert.match(fixture.q('[data-transfers]').textContent,/连接.*收件/);
});

test('Mac receive failures remain visible with a way to choose another folder', () => {
  const fixture=createMiniFixture();
  fixture.state({platform:'Mac',receiveDirectory:'/synthetic/unavailable',receiveSetupRequired:false,receiveError:'接收未完成，请检查收件文件夹、磁盘空间和网络。'});
  assert.equal(fixture.q('[data-receive-needed]').hidden,false);
  assert.match(fixture.q('[data-receive-needed-text]').textContent,/接收未完成/);
  fixture.q('[data-choose-receive]').onclick();
  assert.equal(fixture.last('directory.choose').payload.kind,'receive');
  fixture.state({platform:'Mac',receiveDirectory:'/synthetic/new',receiveError:null});
  assert.equal(fixture.q('[data-receive-needed]').hidden,true);
});

test('clipboard settings explain that only copied text and links may be sent', () => {
  for(const platform of ['Windows','Mac']) {
    const fixture=createMiniFixture();
    fixture.state({platform,clipboardEnabled:true});
    assert.match(fixture.q('[data-clipboard-privacy]').textContent,/新复制的文字和链接可能发送到另一台电脑/);
  }
});
