import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Route, Router, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from './nucleo/servicos/auth.servico';
import { RealtimeServico } from './nucleo/servicos/realtime.servico';
import { Shell } from './layout/shell/shell';
import { routes } from './app.routes';

/** A NAVEGAÇÃO DO PAINEL (NAV-1).
 *
 *  ===================== POR QUE ISTO É TESTE =====================
 *  Reorganizar menu e rotas é a mudança que mais quebra em silêncio: nada estoura, nada aparece
 *  no build, e o sintoma é um link que leva a lugar nenhum — descoberto por quem clicou.
 *
 *  Três coisas ficam travadas aqui:
 *
 *  1. O REDIRECIONAMENTO das rotas antigas, com a ABA certa. `/formularios` e `/canais` tiveram
 *     item de menu próprio e podem estar salvos no navegador de alguém. Mandar as duas para a
 *     primeira aba faria quem salvou o link do QR chegar em formulários sem entender por quê.
 *
 *  2. NENHUMA ROTA APONTA PARA COMPONENTE QUE SAIU DO MENU. Componente órfão já aconteceu neste
 *     projeto (`paginas/em-breve/` ficou roteado e esquecido). Aqui a checagem é o inverso: as
 *     duas telas que viraram painel não podem ter sobrado como rota.
 *
 *  3. O MENU. A ordem e, principalmente, a AUSÊNCIA de "Integrações" — item de menu para
 *     funcionalidade que não existe é a forma mais barata de mentir sobre o produto.
 *  ================================================================ */
describe('navegação', () => {
  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { }
    desconectar() { }
  }

  /** As rotas FILHAS do painel — as que ficam dentro do shell. */
  function filhas(): Route[] {
    return routes.find(r => r.path === '' && r.children)?.children ?? [];
  }

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);
  });

  afterEach(() => { localStorage.clear(); TestBed.resetTestingModule(); });

  // ==================================================================== redirecionamentos
  /** Executa o `redirectTo` da rota, que é uma FUNÇÃO (ela injeta o Router para montar a URL com
   *  query param — `redirectTo` em string não carrega `?aba=`). */
  function destinoDe(path: string): string {
    const rota = filhas().find(r => r.path === path);
    expect(rota).withContext(`a rota /${path} sumiu — link antigo vira 404`).toBeDefined();
    expect(typeof rota!.redirectTo)
      .withContext(`/${path} deveria redirecionar, não carregar componente`).toBe('function');

    return TestBed.runInInjectionContext(
      () => (rota!.redirectTo as (d: unknown) => unknown)({}) as { toString(): string }).toString();
  }

  it('/formularios REDIRECIONA PARA A ABA DE FORMULÁRIOS', () => {
    expect(destinoDe('formularios')).toBe('/captacao');
  });

  it('/canais REDIRECIONA PARA A ABA DE QR — não para a primeira', () => {
    // O ponto do teste é o `?aba=qr`. Sem ele o redirecionamento "funciona" e leva a pessoa para
    // a aba errada, que é pior que um 404: ela não percebe que está no lugar errado.
    expect(destinoDe('canais')).toBe('/captacao?aba=qr');
  });

  it('CAPTAÇÃO EXISTE E SÓ O DONO ENTRA', () => {
    const captacao = filhas().find(r => r.path === 'captacao');
    expect(captacao).withContext('a rota /captacao não existe').toBeDefined();
    expect(captacao!.loadComponent).toBeDefined();
    expect(captacao!.canActivate?.length)
      .withContext('/captacao ficou sem guarda de papel').toBeGreaterThan(0);
  });

  it('NENHUMA ROTA CARREGA OS PAINÉIS COMO TELA', () => {
    // Formulários e QR viraram abas. Se uma delas voltasse a ter `loadComponent`, existiriam dois
    // caminhos para a mesma coisa — um com o cabeçalho da tela, outro sem — e o segundo
    // renderizaria um painel sem título, solto na página.
    for (const path of ['formularios', 'canais']) {
      expect(filhas().find(r => r.path === path)?.loadComponent)
        .withContext(`/${path} voltou a ser tela; deveria só redirecionar`).toBeUndefined();
    }
  });

  it('toda rota do painel ou carrega componente ou redireciona', () => {
    // Rota sem nenhum dos dois é rota morta: ela casa a URL e não mostra nada.
    for (const r of filhas()) {
      const tem = !!r.loadComponent || !!r.component || !!r.redirectTo;
      expect(tem).withContext(`a rota "/${r.path}" não leva a lugar nenhum`).toBeTrue();
    }
  });

  // ==================================================================== menu
  function montarShell(): HTMLElement {
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).match(() => true).forEach(r => r.flush({}));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function menu(): string[] {
    return [...montarShell().querySelectorAll('nav a')]
      .map(a => a.textContent?.trim().split('\n')[0].trim() ?? '');
  }

  it('O GRUPO DE CONFIGURAÇÃO ESTÁ NA ORDEM, COM CAPTAÇÃO NO LUGAR DAS DUAS', () => {
    const itens = menu();
    const config = itens.slice(itens.indexOf('Equipe'));

    // "Integrações" entrou no INT-3, quando o webhook de saída passou a existir. Antes disso o
    // NAV-1 exigia a ausência dele — e a regra não mudou: o item existe porque a tela existe.
    expect(config).toEqual([
      'Equipe', 'Conexão', 'Etapas do funil', 'Captação', 'Integrações', 'Configurações'
    ]);
  });

  it('TODO ITEM DE MENU LEVA A UMA ROTA QUE EXISTE', () => {
    // ===== A REGRA QUE O NAV-1 ESCREVEU, VIRADA EM TESTE =====
    // Lá ela era "não existe item de Integrações", o que valia enquanto a tela não existia e
    // deixou de valer no INT-3. O que NÃO muda de bloco para bloco é isto: nenhum item de menu
    // pode apontar para o vazio — o cliente clica, não encontra nada, e passa a duvidar do resto.
    const rotas = new Set(filhas().map(r => r.path));
    const links = [...(montarShell().querySelectorAll('nav a'))]
      .map(a => a.getAttribute('href')?.replace(/^\//, '').split('?')[0] ?? '');

    expect(links.length).toBeGreaterThan(5);
    for (const href of links) {
      expect(rotas.has(href)).withContext(`o menu aponta para /${href}, que não é rota`).toBeTrue();
    }
  });

  it('os dois itens que viraram abas saíram do menu', () => {
    // Dois caminhos para a mesma tela é o começo de "qual dos dois é o certo?".
    const itens = menu();
    expect(itens).not.toContain('Formulários do site');
    expect(itens).not.toContain('QR Code e links');
  });

  it('a navegação principal continua antes do grupo de configuração', () => {
    const itens = menu();

    expect(itens.slice(0, itens.indexOf('Equipe')))
      .toEqual(['Dashboard', 'Caixa de Entrada', 'Funil', 'Contatos', 'Meu Dia']);
  });

  it('quem não é dono não vê o grupo de configuração', async () => {
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 2, nome: 'Bia', email: 'bia@x.com', papel: 'vendedor', empresaNome: 'Padaria' }
    } as never);

    const itens = menu();
    expect(itens).not.toContain('Captação');
    expect(itens).toContain('Caixa de Entrada');
  });

  it('o link de Captação leva para /captacao', async () => {
    // O guard é UX; o enforcement real é o `[Authorize(Roles="dono")]` do controller. O que se
    // prova aqui é que a URL do menu casa com uma rota de verdade.
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/captacao');
    expect(router.url).toBe('/captacao');
  });
});
