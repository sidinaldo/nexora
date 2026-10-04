import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { GESTOS_DELEGAVEIS } from '../../nucleo/seguranca/gestos';
import { Permissao, UsuarioEquipe } from '../../nucleo/modelos';
import { Equipe } from './equipe';

/** ===================== A TELA DE EQUIPE, E AS PERMISSÕES POR PESSOA (PER-1) =====================
///
 *  ⚠️ ESTA TELA TINHA 183 LINHAS E NENHUM SPEC. A permissão por pessoa nasceu em cima dela, então
 *  o arquivo começa aqui — e o que ele guarda é o acordo com o servidor: o que o dono vê é o que a
 *  pessoa pode, e o que ele marca é o que o servidor recebe.
 *
 *  O enforcement é da API; nada aqui prova autorização. O que se prova é que a tela não oferece o
 *  que vai levar 403, e não manda o contrário do que mostra.
 *  ============================================================================================ */
describe('equipe — permissões por pessoa', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<Equipe>;

  const DONO: UsuarioEquipe = {
    id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono', status: 'ativo',
    ultimoAcessoEm: null, permissoes: PERMISSOES_DE.dono
  };

  /** Um vendedor com `cancelar_venda` concedida — o caso que a feature existe para resolver. */
  const VENDEDOR: UsuarioEquipe = {
    id: 2, nome: 'Rafael Lima', email: 'rafael@x.com', papel: 'vendedor', status: 'ativo',
    ultimoAcessoEm: null, permissoes: ['cancelar_venda']
  };

  const GESTORA: UsuarioEquipe = {
    id: 3, nome: 'Beatriz Souza', email: 'bia@x.com', papel: 'gestor', status: 'ativo',
    ultimoAcessoEm: null, permissoes: PERMISSOES_DE.gestor
  };

  async function montar(equipe: UsuarioEquipe[] = [DONO, VENDEDOR, GESTORA]) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    // A sessão é do DONO: só ele chega nesta tela.
    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: {
        id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria do Bairro'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Equipe);
    fixture.detectChanges();

    http.expectOne(r => r.url.includes('/equipe')).flush(equipe);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  /** Abre o modal de edição de uma pessoa. */
  function editar(raiz: HTMLElement, quem: UsuarioEquipe) {
    fixture.componentInstance.abrirEdicao(quem);
    fixture.detectChanges();
    return raiz;
  }

  function interruptores(raiz: HTMLElement) {
    return [...raiz.querySelectorAll<HTMLInputElement>('.permissoes input[type="checkbox"]')];
  }

  function rotulos(raiz: HTMLElement) {
    return [...raiz.querySelectorAll('.permissoes .marcador > span > span:first-child')]
      .map(e => e.textContent?.trim());
  }

  // ==================================================================== o que a tela mostra

  /** ⚠️ MARCADO PELO QUE O SERVIDOR MANDOU, e não deduzido do papel. É o contrato do painel
   *  inteiro desde que `ehDono` saiu: a tela pergunta o que a pessoa pode, nunca adivinha. */
  it('OS INTERRUPTORES NASCEM COM O QUE A PESSOA PODE HOJE', async () => {
    const raiz = editar(await montar(), VENDEDOR);

    const marcados = interruptores(raiz)
      .map((c, i) => c.checked ? rotulos(raiz)[i] : null)
      .filter(Boolean);

    expect(marcados).withContext('a exceção que o dono já tinha dado').toEqual(['Cancelar venda']);
    expect(interruptores(raiz).length).withContext('os dez delegáveis').toBe(10);
  });

  /** ⚠️ OS DOIS INDELEGÁVEIS NÃO APARECEM. `gerenciar_equipe` deixaria um vendedor promover um
   *  colega a Dono; `configurar_empresa` entregaria o webhook de saída, que manda a base de
   *  contatos para qualquer URL. Marcá-los não abriria acesso — o servidor recusa —, mas seria um
   *  interruptor que não faz nada. */
  it('OS GESTOS QUE NÃO SE DELEGAM NÃO TÊM INTERRUPTOR', async () => {
    const raiz = editar(await montar(), VENDEDOR);

    const chaves = GESTOS_DELEGAVEIS.map(g => g.chave);
    expect(chaves).not.toContain('gerenciar_equipe' as Permissao);
    expect(chaves).not.toContain('configurar_empresa' as Permissao);

    expect(rotulos(raiz)).not.toContain('Equipe');
    expect(rotulos(raiz)).not.toContain('Configurações');
  });

  it('DONO NÃO TEM INTERRUPTOR — ELE PODE TUDO', async () => {
    const raiz = editar(await montar(), { ...DONO, id: 9 });

    expect(interruptores(raiz).length).toBe(0);
    expect(raiz.querySelector('.permissoes')?.textContent)
      .toContain('Dono pode tudo');
  });

  /** O dono não ajusta a PRÓPRIA permissão, pelo mesmo motivo que não muda o próprio papel. */
  it('NÃO HÁ INTERRUPTOR PARA MIM MESMO', async () => {
    const raiz = editar(await montar(), DONO);

    expect(raiz.querySelector('.permissoes')).toBeNull();
  });

  // ==================================================================== a trava do papel

  /** ===================== O BUG QUE ESTA TRAVA EVITA =====================
   *  O dono abre um VENDEDOR (quase tudo desligado), troca o seletor para Gestor e salva. Sem a
   *  trava, a tela mandaria a lista de um vendedor com papel=gestor — e o servidor gravaria
   *  negações. "Promovi para gestor e ele continua sem ver os números."
   *  ====================================================================== */
  it('TROCAR O PAPEL TRAVA OS INTERRUPTORES E DIZ POR QUÊ', async () => {
    const raiz = editar(await montar(), VENDEDOR);

    fixture.componentInstance.edPapel.set('gestor');
    fixture.detectChanges();

    expect(interruptores(raiz).every(c => c.disabled))
      .withContext('nenhum deles aceita clique').toBeTrue();

    expect(raiz.querySelector('.aviso-papel')?.textContent)
      .toContain('redefine estas marcações');
  });

  // ==================================================================== o que a tela manda

  it('SALVAR MANDA A LISTA EFETIVA JUNTO DO PAPEL', async () => {
    const raiz = editar(await montar(), VENDEDOR);

    fixture.componentInstance.alternarGesto('gerenciar_etiquetas');
    fixture.detectChanges();
    fixture.componentInstance.salvarEdicao();

    const req = http.expectOne(r => r.method === 'PUT' && r.url.includes('/equipe/2'));
    expect(req.request.body.papel).toBe('vendedor');
    expect([...req.request.body.permissoes].sort())
      .toEqual(['cancelar_venda', 'gerenciar_etiquetas']);

    req.flush(null);
    http.expectOne(r => r.url.includes('/equipe')).flush([]);
  });

  /** ⚠️ COM O PAPEL TROCADO, A LISTA NÃO VAI. O servidor a descartaria de qualquer jeito; mandar
   *  mesmo assim faria a tela pedir o contrário do que a nota ao lado promete. */
  it('COM O PAPEL TROCADO, SALVAR NÃO MANDA LISTA NENHUMA', async () => {
    const raiz = editar(await montar(), VENDEDOR);

    fixture.componentInstance.edPapel.set('gestor');
    fixture.detectChanges();
    fixture.componentInstance.salvarEdicao();

    const req = http.expectOne(r => r.method === 'PUT' && r.url.includes('/equipe/2'));
    expect(req.request.body.papel).toBe('gestor');
    expect(req.request.body.permissoes).toBeUndefined();

    req.flush(null);
    http.expectOne(r => r.url.includes('/equipe')).flush([]);
    expect(raiz).toBeTruthy();
  });

  /** O atalho de inativar da lista manda só nome, papel e situação — e é isso que impede inativar
   *  alguém de apagar em silêncio tudo que o dono tinha marcado para ele. */
  it('O ATALHO DE INATIVAR NÃO MANDA PERMISSÃO', async () => {
    await montar();

    spyOn(window, 'confirm').and.returnValue(true);
    fixture.componentInstance.mudarStatus(VENDEDOR, 'inativo');

    const req = http.expectOne(r => r.method === 'PUT' && r.url.includes('/equipe/2'));
    expect(req.request.body.permissoes).toBeUndefined();

    req.flush(null);
    http.expectOne(r => r.url.includes('/equipe')).flush([]);
  });

  // ==================================================================== o catálogo

  /** ⚠️ UM GESTO NOVO SEM TEXTO NASCERIA COMO INTERRUPTOR SEM NOME. É a única coisa que lembra
   *  quem acrescentar uma `Permissao` delegável de escrever o que ela faz — e a descrição importa
   *  mais aqui do que em qualquer outra tela: ela é o que o dono lê antes de entregar o poder. */
  it('TODO GESTO DELEGÁVEL TEM RÓTULO E DESCRIÇÃO', async () => {
    await montar();

    for (const g of GESTOS_DELEGAVEIS) {
      expect(g.rotulo.length).withContext(g.chave).toBeGreaterThan(2);
      expect(g.descricao.length).withContext(g.chave).toBeGreaterThan(10);
      expect(g.descricao.endsWith('.')).withContext(`${g.chave}: frase inteira`).toBeTrue();
    }

    expect(GESTOS_DELEGAVEIS.length).withContext('os dez delegáveis').toBe(10);
    expect(new Set(GESTOS_DELEGAVEIS.map(g => g.chave)).size).toBe(10);
  });
});
