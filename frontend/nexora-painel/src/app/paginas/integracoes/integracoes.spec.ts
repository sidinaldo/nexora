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
        ? {
          credencial: null, leadsComAnuncio30Dias: 0, conversoes: [],
          // INT-5: objeto, nao lista — a tela le `.total` dele direto.
          vendasSemEnvio: { total: 0, valorTotal: 0, noPrazo: 0, diasDaJanela: 21, vendas: [] }
        }
        : { webhook: null, entregas: [] });
    }
    fixture.detectChanges();
  }

  it('SÃO QUATRO ABAS, e a que abre é ANÚNCIOS', () => {
    /* ⚠️ A ORDEM NÃO É ESTÉTICA, E ELA JÁ FOI A OUTRA.
       A versão anterior abria no webhook, com este argumento: `/integracoes` já existia e tinha só
       ele, então abrir em "Anúncios" mandaria quem salvou o link para uma tela que não pediu.

       Era um bom argumento, e ele valia para uma base instalada. Não há nenhuma — o produto ainda
       não tem cliente em produção, e a janela de trocar isso de graça fecha no primeiro.

       O critério que sobra é a quem cada aba serve. ANÚNCIOS serve a todo cliente: conectar o pixel
       faz a venda fechada aqui voltar para a Meta, e isso vale inclusive para quem só tem WhatsApp
       e nenhum site. WEBHOOK serve a quem tem OUTRO sistema para avisar — a minoria, e quem precisa
       dele sabe que precisa. Abrir na aba que a maioria não usa faz a tela parecer não ser para
       ela. */
    montar();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelectorAll('[role="tab"]').length).toBe(4);
    expect(c.aba()).toBe('anuncios');
    expect(raiz.querySelector('app-integracoes-anuncios')).not.toBeNull();

    // `@if` e não CSS: a aba fechada não fica com requisição pendente.
    expect(raiz.querySelector('app-integracoes-webhook')).toBeNull();

    // E os botões seguem a mesma ordem do conteúdo — Anúncios primeiro, cada integração com o
    // histórico dela logo ao lado (UI-XX).
    const rotulos = [...raiz.querySelectorAll('[role="tab"]')].map(b => b.textContent!.trim());
    expect(rotulos).toEqual(['Anúncios', 'Envios à Meta', 'Webhook', 'Entregas do webhook']);
  });

  /** UI-XX · as duas abas de uma integração são O MESMO painel: ir de uma para a outra só troca a
   *  parte mostrada, sem pedir nada de novo ao servidor. */
  it('IR DE ANÚNCIOS PARA ENVIOS À META NÃO RECARREGA', () => {
    montar();
    const antes = (fixture.nativeElement as HTMLElement).querySelector('app-integracoes-anuncios');

    c.trocarAba('envios');
    fixture.detectChanges();

    const depois = (fixture.nativeElement as HTMLElement).querySelector('app-integracoes-anuncios');
    expect(depois).toBe(antes);
    http.expectNone(() => true);
  });

  it('`?aba=entregas` ABRE O PAINEL DO WEBHOOK NA PARTE DAS ENTREGAS', () => {
    montar('entregas');

    expect(c.aba()).toBe('entregas');
    expect((fixture.nativeElement as HTMLElement).querySelector('app-integracoes-webhook')).not.toBeNull();
  });

  /** A falha não pode sumir numa aba que ninguém abre: o número vai no rótulo dela. */
  it('AS FALHAS DO PERÍODO APARECEM NO RÓTULO DA ABA DO HISTÓRICO', () => {
    montar();

    c.falhasMeta.set(3);
    fixture.detectChanges();

    const envios = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[role="tab"]')]
      .find(b => b.textContent!.includes('Envios à Meta'))!;
    expect(envios.textContent).toContain('3 falharam');
  });

  it('`?aba=webhook` ABRE NA ABA DE WEBHOOK', () => {
    // É o que permite mandar "abre em Integrações, aba Webhook" por mensagem. Agora é o webhook que
    // precisa do parâmetro, porque o padrão virou o outro.
    montar('webhook');

    expect(c.aba()).toBe('webhook');
    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-integracoes-webhook')).not.toBeNull();
    expect(raiz.querySelector('app-integracoes-anuncios')).toBeNull();
  });

  it('`?aba=anuncios` CONTINUA VALENDO', () => {
    // ⚠️ O passo "Conecte seus anúncios" de Primeiros passos aponta para `/integracoes?aba=anuncios`
    // (ServicoOnboarding). O parâmetro virou redundante, mas não pode ter virado inválido.
    montar('anuncios');

    expect(c.aba()).toBe('anuncios');
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('app-integracoes-anuncios')).not.toBeNull();
  });

  it('TROCAR DE ABA troca o painel', () => {
    montar();

    c.trocarAba('webhook');
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-integracoes-webhook')).not.toBeNull();
    expect(raiz.querySelector('app-integracoes-anuncios')).toBeNull();
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
