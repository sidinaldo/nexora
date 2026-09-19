import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { Observable, firstValueFrom, isObservable } from 'rxjs';
import { AuthServico } from '../servicos/auth.servico';
import { Permissao } from '../modelos';
import { guardaAutenticado, guardaPermissao } from './guardas';
import { PERMISSOES_DE } from './permissoes-de-teste';

/** Os guards são conveniência de UX — o enforcement real é a política da rota no servidor. Mas
 *  conveniência que falha ABRINDO é outra coisa: a tela de equipe carregaria para um vendedor e
 *  só quebraria no 403, depois de já ter mostrado o que não devia.
 *
 *  ⚠️ ERAM `guardaDono` E `guardaGestor`, com o papel comparado aqui. Agora a guarda pergunta a
 *  PERMISSÃO que o servidor mandou — e os testes abaixo provam que ela não olha o papel. */
describe('guardas de rota', () => {
  let auth: AuthServico;
  let injector: EnvironmentInjector;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([])
      ]
    });

    auth = TestBed.inject(AuthServico);
    injector = TestBed.inject(EnvironmentInjector);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => localStorage.clear());

  type Resultado = boolean | UrlTree | Observable<boolean | UrlTree>;

  /** CanActivateFn usa `inject()`, então precisa rodar dentro de um contexto de injeção. */
  function rodar(guarda: ReturnType<typeof guardaPermissao>): Resultado {
    return runInInjectionContext(injector, () =>
      guarda({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) as Resultado;
  }

  function entrar(papel: string, permissoes?: Permissao[]) {
    auth.aplicarLogin({
      token: 't',
      usuario: { id: 1, nome: 'X', email: 'x@x.com', papel, empresaNome: 'E', permissoes }
    } as never);
  }

  /** O guard não devolve `false`: devolve uma UrlTree, que é o redirecionamento. Um `false`
   *  seco deixaria o usuário parado na rota anterior, sem explicação. */
  function destinoDoRedirecionamento(r: Resultado): string {
    expect(r instanceof UrlTree).withContext('deveria redirecionar, não devolver booleano').toBeTrue();
    return (r as UrlTree).toString();
  }

  describe('guardaAutenticado', () => {
    it('deixa passar com sessão', () => {
      entrar('vendedor', []);
      expect(runInInjectionContext(injector, () =>
        guardaAutenticado({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBeTrue();
    });

    it('sem sessão, manda para o login', () => {
      const r = runInInjectionContext(injector, () =>
        guardaAutenticado({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) as UrlTree;
      expect(r.toString()).toBe('/entrar');
    });
  });

  describe('guardaPermissao', () => {
    it('deixa passar quem tem a permissão', () => {
      entrar('dono', PERMISSOES_DE.dono);
      expect(rodar(guardaPermissao('configurar_empresa'))).toBeTrue();
      expect(rodar(guardaPermissao('gerenciar_equipe'))).toBeTrue();
    });

    it('BARRA quem não tem, mandando para a caixa', () => {
      for (const papel of ['gestor', 'vendedor'] as const) {
        entrar(papel, PERMISSOES_DE[papel]);
        expect(destinoDoRedirecionamento(rodar(guardaPermissao('configurar_empresa'))))
          .withContext(`${papel} não configura a empresa`).toBe('/caixa');
      }
    });

    it('barra quem não tem sessão nenhuma', () => {
      expect(destinoDoRedirecionamento(rodar(guardaPermissao('configurar_empresa')))).toBe('/caixa');
    });

    /** ⚠️ O PAPEL NÃO DECIDE NADA. Um "dono" cuja lista não traz a permissão é barrado — e um
     *  papel que o painel nem conhece passa, se o servidor disse que pode. Se a guarda voltasse a
     *  comparar `papel === 'dono'`, os dois lados deste teste inverteriam. */
    it('o papel não decide: vale a lista que o servidor mandou', () => {
      entrar('dono', ['importar_contatos']);
      expect(destinoDoRedirecionamento(rodar(guardaPermissao('configurar_empresa')))).toBe('/caixa');

      entrar('supervisor', ['configurar_empresa']);
      expect(rodar(guardaPermissao('configurar_empresa'))).toBeTrue();
    });

    /** ⚠️ A SESSÃO ABERTA ANTES DE A LISTA EXISTIR. Ela guardou o usuário sem `permissoes`, e
     *  barrar aqui mandaria o dono para a caixa ao abrir o link salvo de Equipe. A guarda pergunta
     *  ao servidor e decide com a resposta. */
    it('sessão sem a lista pergunta ao servidor antes de decidir', async () => {
      entrar('dono');   // sem `permissoes`

      const r = rodar(guardaPermissao('gerenciar_equipe'));
      expect(isObservable(r)).withContext('deveria perguntar, e não decidir na hora').toBeTrue();

      const decisao = firstValueFrom(r as Observable<boolean | UrlTree>);
      http.expectOne(req => req.url.endsWith('/auth/permissoes')).flush(PERMISSOES_DE.dono);

      expect(await decisao).toBeTrue();
      expect(auth.pode('gerenciar_equipe')).withContext('a resposta ficou guardada').toBeTrue();
    });

    it('e se o servidor não responder, barra', async () => {
      entrar('dono');

      const decisao = firstValueFrom(rodar(guardaPermissao('gerenciar_equipe')) as Observable<boolean | UrlTree>);
      http.expectOne(req => req.url.endsWith('/auth/permissoes'))
        .flush(null, { status: 500, statusText: 'Erro' });

      expect(destinoDoRedirecionamento(await decisao)).toBe('/caixa');
    });
  });
});
