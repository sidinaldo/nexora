import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Integracoes } from './integracoes';

/** INTEGRAÇÕES — o container das abas (INT-4).
 *
 *  O que ele faz é cabeçalho e abas; o conteúdo é dos painéis, que se testam sozinhos
 *  (`webhook/webhook.spec.ts` e o painel de anúncios). O que este arquivo protege é o que só
 *  existe aqui: que a aba padrão seja o webhook — quem tem link salvo para `/integracoes` espera
 *  cair na integração que já existia —, que `?aba=` funcione, e que o cabeçalho de página more
 *  num lugar só. */
describe('integrações — as abas', () => {
  let fixture: ComponentFixture<Integracoes>;
  let c: Integracoes;
  let http: HttpTestingController;

  function montar(abaNaUrl?: string) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap(abaNaUrl ? { aba: abaNaUrl } : {})
            }
          }
        }
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Integracoes);
    c = fixture.componentInstance;
    fixture.detectChanges();

    // O painel que abriu busca os próprios dados. Qualquer corpo serve: este arquivo não é sobre
    // o conteúdo deles.
    for (const r of http.match(() => true)) {
      r.flush(r.request.url.includes('/conversoes')
        ? { credencial: null, leadsComAnuncio30Dias: 0 }
        : { webhook: null, entregas: [] });
    }
    fixture.detectChanges();
  }

  it('SÃO DUAS ABAS, e a que abre é o webhook', () => {
    // ⚠️ A ORDEM NÃO É ESTÉTICA: `/integracoes` já existia e tinha só o webhook. Abrir em
    // "Anúncios" mandaria quem salvou o link para uma tela que ele não pediu.
    montar();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelectorAll('[role="tab"]').length).toBe(2);
    expect(c.aba()).toBe('webhook');
    expect(raiz.querySelector('app-integracoes-webhook')).not.toBeNull();

    // `@if` e não CSS: a aba fechada não fica com requisição pendente.
    expect(raiz.querySelector('app-integracoes-anuncios')).toBeNull();
  });

  it('`?aba=anuncios` ABRE NA ABA DE ANÚNCIOS', () => {
    // É o que permite mandar "abre em Integrações, aba Anúncios" por mensagem — e o que o passo de
    // "Primeiros passos" vai usar para levar a pessoa direto ao lugar de conectar.
    montar('anuncios');

    expect(c.aba()).toBe('anuncios');
    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-integracoes-anuncios')).not.toBeNull();
    expect(raiz.querySelector('app-integracoes-webhook')).toBeNull();
  });

  it('TROCAR DE ABA troca o painel', () => {
    montar();

    c.trocarAba('anuncios');
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-integracoes-anuncios')).not.toBeNull();
    expect(raiz.querySelector('app-integracoes-webhook')).toBeNull();
  });

  it('O CABEÇALHO DE PÁGINA MORA AQUI, e só aqui', () => {
    // Se o painel também trouxesse `.pagina` e `<h1>`, a tela teria dois títulos e dois recuos.
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    expect(raiz.querySelectorAll('.pagina').length).toBe(1);
    expect(raiz.querySelectorAll('h1').length).toBe(1);

    // É tela DENSA: sem o modificador de formulário. A asserção morava no spec do webhook e veio
    // com o `.pagina`.
    expect(raiz.querySelector('.pagina')!.classList.contains('formulario')).toBeFalse();
  });
});
