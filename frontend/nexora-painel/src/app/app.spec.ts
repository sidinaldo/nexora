import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RouterOutlet } from '@angular/router';
import { provideRouter } from '@angular/router';
import { App } from './app';

/** A raiz é só o casco do roteador — quem desenha é a rota. O teste gerado pelo CLI procurava
 *  o "Hello, nexora-painel" do esqueleto e ficou VERMELHO desde a primeira tela de verdade;
 *  suíte vermelha de nascença é pior que suíte vazia, porque ensina a ignorar o vermelho. */
describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideZonelessChangeDetection(), provideRouter([])]
    }).compileComponents();
  });

  it('monta', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('tem o router-outlet — sem ele nenhuma rota desenha', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect(fixture.debugElement.query(d => d.componentInstance instanceof RouterOutlet)
      ?? fixture.nativeElement.querySelector('router-outlet')).toBeTruthy();
  });
});
